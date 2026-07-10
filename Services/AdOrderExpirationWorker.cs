using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PremierAPI.Services
{
    public class AdOrderExpirationWorker : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly ActiveDirectoryService _ad;
        private readonly ILogger<AdOrderExpirationWorker> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeZoneInfo _timeZone;
        private readonly bool _enabled;

        public AdOrderExpirationWorker(IConfiguration config, ActiveDirectoryService ad, ILogger<AdOrderExpirationWorker> logger)
        {
            _config = config;
            _ad = ad;
            _logger = logger;
            _enabled = config.GetValue<bool?>("AdExpiration:Enabled") ?? true;
            int intervalSeconds = Math.Max(30, config.GetValue<int?>("AdExpiration:CheckIntervalSeconds") ?? 60);
            _interval = TimeSpan.FromSeconds(intervalSeconds);
            _timeZone = ResolveTimeZone(config["AdExpiration:TimeZone"] ?? "");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                _logger.LogInformation("[AD EXPIRACAO] Rotina automatica desativada por configuracao.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessExpiredOrdersAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AD EXPIRACAO] Falha geral na rotina de expiracao.");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task ProcessExpiredOrdersAsync(CancellationToken cancellationToken)
        {
            string connString = _config.GetConnectionString("DefaultConnection") ?? "";
            if (string.IsNullOrWhiteSpace(connString)) return;

            DateTime now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
            using var db = new NpgsqlConnection(connString);

            var orders = (await db.QueryAsync<ExpiringOrder>(@"
                SELECT
                    o.id AS Id,
                    o.user_id AS UserId,
                    u.name AS UserName,
                    u.email AS Email,
                    u.ad_username AS AdUsername,
                    o.created_at AS CreatedAt,
                    o.days AS Days,
                    COALESCE(o.ad_missing_link_alerted, false) AS AdMissingLinkAlerted
                FROM orders o
                JOIN users u ON u.id = o.user_id
                WHERE o.status = 'pago'
                  AND COALESCE(o.ad_expiration_processed, false) = false
                  AND (o.created_at::date + o.days) <= @Today
                ORDER BY o.created_at ASC
                LIMIT 100",
                new { Today = now.Date })).ToList();

            foreach (var order in orders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTime dueAt = GetDueAt(order.CreatedAt, order.Days);
                if (now < dueAt) continue;

                try
                {
                    if (await HasAnotherActivePaidOrderAsync(db, order, now))
                    {
                        await MarkExpirationProcessedAsync(db, order.Id, now);
                        _logger.LogInformation("[AD EXPIRACAO] Pedido {OrderId} expirado ignorado porque o usuario {Email} possui outro pedido ativo.", order.Id, order.Email);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(order.AdUsername))
                    {
                        if (!order.AdMissingLinkAlerted)
                        {
                            _logger.LogWarning("[AD EXPIRACAO] Pedido {OrderId} venceu em {DueAt:yyyy-MM-dd HH:mm}, mas o usuario {Email} nao possui usuario AD vinculado.", order.Id, dueAt, order.Email);
                            await db.ExecuteAsync("UPDATE orders SET ad_missing_link_alerted = true WHERE id = @Id", new { order.Id });
                        }
                        continue;
                    }

                    await _ad.DisableAndArchiveUserAsync(order.AdUsername);
                    await MarkExpirationProcessedAsync(db, order.Id, now);
                    _logger.LogWarning("[AD EXPIRACAO] Usuario AD {AdUsername} inativado automaticamente pelo vencimento do pedido {OrderId}.", order.AdUsername, order.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AD EXPIRACAO] Falha ao processar pedido {OrderId} para {Email}.", order.Id, order.Email);
                }
            }
        }

        private static async Task<bool> HasAnotherActivePaidOrderAsync(NpgsqlConnection db, ExpiringOrder expiredOrder, DateTime now)
        {
            var orders = await db.QueryAsync<OrderDateInfo>(
                "SELECT created_at AS CreatedAt, days AS Days FROM orders WHERE user_id = @UserId AND id <> @OrderId AND status = 'pago'",
                new { expiredOrder.UserId, OrderId = expiredOrder.Id });

            return orders.Any(order => GetDueAt(order.CreatedAt, order.Days) > now);
        }

        private static Task MarkExpirationProcessedAsync(NpgsqlConnection db, Guid orderId, DateTime now)
        {
            return db.ExecuteAsync(
                "UPDATE orders SET ad_expiration_processed = true, ad_expiration_processed_at = @Now WHERE id = @Id",
                new { Id = orderId, Now = now });
        }

        private static DateTime GetDueAt(DateTime createdAt, int days)
        {
            return createdAt.Date.AddDays(days).AddHours(23).AddMinutes(59);
        }

        private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch { return TimeZoneInfo.Local; }
        }

        private sealed class ExpiringOrder
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public string UserName { get; set; } = "";
            public string Email { get; set; } = "";
            public string? AdUsername { get; set; }
            public DateTime CreatedAt { get; set; }
            public int Days { get; set; }
            public bool AdMissingLinkAlerted { get; set; }
        }

        private sealed class OrderDateInfo
        {
            public DateTime CreatedAt { get; set; }
            public int Days { get; set; }
        }
    }
}
