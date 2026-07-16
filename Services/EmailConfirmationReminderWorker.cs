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
    public class EmailConfirmationReminderWorker : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly EmailConfirmationService _emailConfirmation;
        private readonly ILogger<EmailConfirmationReminderWorker> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeZoneInfo _timeZone;
        private readonly bool _enabled;

        public EmailConfirmationReminderWorker(
            IConfiguration config,
            EmailConfirmationService emailConfirmation,
            ILogger<EmailConfirmationReminderWorker> logger)
        {
            _config = config;
            _emailConfirmation = emailConfirmation;
            _logger = logger;
            _enabled = config.GetValue<bool?>("EmailConfirmationReminders:Enabled") ?? true;
            int intervalSeconds = Math.Max(60, config.GetValue<int?>("EmailConfirmationReminders:CheckIntervalSeconds") ?? 300);
            _interval = TimeSpan.FromSeconds(intervalSeconds);
            _timeZone = ResolveTimeZone(config["EmailConfirmationReminders:TimeZone"] ?? "");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                _logger.LogInformation("[EMAIL CONFIRMACAO] Reenvios automaticos desativados por configuracao.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessDueRemindersAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EMAIL CONFIRMACAO] Falha geral na rotina de reenvio.");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task ProcessDueRemindersAsync(CancellationToken cancellationToken)
        {
            string connectionString = _config.GetConnectionString("DefaultConnection") ?? "";
            if (string.IsNullOrWhiteSpace(connectionString)) return;

            DateTime now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
            using var db = new NpgsqlConnection(connectionString);
            var users = (await db.QueryAsync<PendingConfirmation>(@"
                SELECT id AS Id, name AS Name, email AS Email,
                       email_confirmation_token AS Token,
                       COALESCE(email_confirmation_resend_count, 0) AS ResendCount,
                       created_at AS CreatedAt
                FROM users
                WHERE email_confirmed = false
                  AND email_confirmation_token IS NOT NULL
                  AND COALESCE(email_confirmation_resend_count, 0) < 2
                  AND email_confirmation_next_send_at IS NOT NULL
                  AND email_confirmation_next_send_at <= @Now
                ORDER BY email_confirmation_next_send_at
                LIMIT 50", new { Now = now })).ToList();

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int claimed = await db.ExecuteAsync(@"
                    UPDATE users
                    SET email_confirmation_next_send_at = @ClaimUntil
                    WHERE id = @Id AND email_confirmed = false
                      AND COALESCE(email_confirmation_resend_count, 0) = @ResendCount
                      AND email_confirmation_next_send_at <= @Now",
                    new { user.Id, user.ResendCount, Now = now, ClaimUntil = now.AddMinutes(30) });
                if (claimed == 0) continue;

                try
                {
                    await _emailConfirmation.SendAsync(user.Email, user.Name, user.Token, cancellationToken);
                    int nextCount = user.ResendCount + 1;
                    DateTime secondReminderAt = user.CreatedAt.Date.AddDays(2).AddHours(19);
                    if (secondReminderAt.Date <= now.Date)
                        secondReminderAt = now.Date.AddDays(1).AddHours(19);
                    DateTime? nextSendAt = nextCount < 2 ? secondReminderAt : null;

                    await db.ExecuteAsync(@"
                        UPDATE users
                        SET email_confirmation_resend_count = @NextCount,
                            email_confirmation_last_sent_at = @Now,
                            email_confirmation_next_send_at = @NextSendAt
                        WHERE id = @Id AND email_confirmed = false
                          AND COALESCE(email_confirmation_resend_count, 0) = @PreviousCount",
                        new { user.Id, NextCount = nextCount, PreviousCount = user.ResendCount, Now = now, NextSendAt = nextSendAt });

                    if (nextSendAt.HasValue)
                    {
                        _logger.LogInformation(
                            "[EMAIL CONFIRMACAO] Reenvio {Count}/2 concluido para o usuario {UserId}. Proximo reenvio agendado para {NextSendAt:dd/MM/yyyy HH:mm}.",
                            nextCount,
                            user.Id,
                            nextSendAt.Value);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[EMAIL CONFIRMACAO] Reenvio {Count}/2 concluido para o usuario {UserId}. Limite de reenvios automaticos atingido.",
                            nextCount,
                            user.Id);
                    }
                }
                catch (Exception ex)
                {
                    await db.ExecuteAsync(@"
                        UPDATE users
                        SET email_confirmation_next_send_at = @RetryAt
                        WHERE id = @Id AND email_confirmed = false",
                        new { user.Id, RetryAt = now.AddMinutes(30) });
                    _logger.LogError(ex, "[EMAIL CONFIRMACAO] Falha no reenvio automatico para o usuario {UserId}.", user.Id);
                }
            }
        }

        public static DateTime GetFirstReminderAt(DateTime localRegistrationTime)
        {
            return localRegistrationTime.Date.AddDays(1).AddHours(11);
        }

        private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch { return TimeZoneInfo.Local; }
        }

        private sealed class PendingConfirmation
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = "";
            public string Email { get; set; } = "";
            public string Token { get; set; } = "";
            public int ResendCount { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}
