using System;
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
    public class AdAccountProvisioningWorker : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly AdAccountProvisioningService _provisioning;
        private readonly ILogger<AdAccountProvisioningWorker> _logger;
        private readonly TimeSpan _interval;

        public AdAccountProvisioningWorker(
            IConfiguration config,
            AdAccountProvisioningService provisioning,
            ILogger<AdAccountProvisioningWorker> logger)
        {
            _config = config;
            _provisioning = provisioning;
            _logger = logger;
            int intervalSeconds = Math.Max(60, config.GetValue<int?>("AdProvisioning:CheckIntervalSeconds") ?? 300);
            _interval = TimeSpan.FromSeconds(intervalSeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AD PROVISIONAMENTO] Falha geral na rotina de conciliacao.");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task ProcessPendingAsync(CancellationToken cancellationToken)
        {
            string connectionString = _config.GetConnectionString("DefaultConnection") ?? "";
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            using var db = new NpgsqlConnection(connectionString);
            var orderIds = (await db.QueryAsync<Guid>(@"
                SELECT id
                FROM orders
                WHERE status = 'pago'
                  AND ad_provisioned_at IS NULL
                  AND (ad_provisioning_next_attempt_at IS NULL OR ad_provisioning_next_attempt_at <= CURRENT_TIMESTAMP)
                ORDER BY created_at
                LIMIT 25")).ToList();

            foreach (Guid orderId in orderIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _provisioning.TryProvisionOrderAsync(orderId, cancellationToken);
            }
        }
    }
}
