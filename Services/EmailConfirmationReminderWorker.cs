using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PremierAPI.Services
{
    public class EmailConfirmationReminderWorker : BackgroundService
    {
        private readonly IEmailConfirmationSender _emailConfirmation;
        private readonly ILogger<EmailConfirmationReminderWorker> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeZoneInfo _timeZone;
        private readonly bool _enabled;
        private readonly EmailConfirmationTokenService _confirmationTokens;

        public EmailConfirmationReminderWorker(
            IConfiguration config,
            IEmailConfirmationSender emailConfirmation,
            ILogger<EmailConfirmationReminderWorker> logger,
            EmailConfirmationTokenService confirmationTokens)
        {
            _emailConfirmation = emailConfirmation;
            _logger = logger;
            _confirmationTokens = confirmationTokens;
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

        internal async Task ProcessDueRemindersAsync(CancellationToken cancellationToken)
        {
            DateTime now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
            await _confirmationTokens.CleanupAsync(cancellationToken);
            for (int processed = 0; processed < 50; processed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EmailConfirmationTokenIssue? issue =
                    await _confirmationTokens.PrepareDueReminderAsync(
                        now,
                        cancellationToken);
                if (issue == null) break;

                try
                {
                    await _emailConfirmation.SendAsync(
                        issue.Email,
                        issue.Name,
                        issue.Token,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    try
                    {
                        await _confirmationTokens.MarkFailedAsync(
                            issue.TokenId,
                            EmailConfirmationFailureSanitizer.Code(ex),
                            now.AddMinutes(30),
                            CancellationToken.None);
                    }
                    catch (Exception persistenceException)
                    {
                        _logger.LogError(
                            persistenceException,
                            "[EMAIL CONFIRMACAO] Falha ao registrar resultado sanitizado do lembrete para {UserId}.",
                            issue.UserId);
                    }
                    _logger.LogError(
                        EmailConfirmationFailureSanitizer.SafeException(ex),
                        "[EMAIL CONFIRMACAO] Falha no reenvio automatico para o usuario {UserId}.",
                        issue.UserId);
                    if (ex is OperationCanceledException &&
                        cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    continue;
                }

                EmailConfirmationDeliveryResult delivery;
                try
                {
                    delivery = await _confirmationTokens.MarkSentAsync(
                        issue.TokenId,
                        now,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[EMAIL CONFIRMACAO] Lembrete aceito pelo SMTP, mas a marcacao de sucesso falhou para {UserId}.",
                        issue.UserId);
                    continue;
                }

                if (delivery.ReminderCount.HasValue &&
                    delivery.NextSendAt.HasValue)
                {
                    _logger.LogInformation(
                        "[EMAIL CONFIRMACAO] Reenvio {Count}/2 concluido para o usuario {UserId}. Proximo reenvio agendado para {NextSendAt:dd/MM/yyyy HH:mm}.",
                        delivery.ReminderCount.Value,
                        issue.UserId,
                        delivery.NextSendAt.Value);
                }
                else if (delivery.ReminderCount.HasValue)
                {
                    _logger.LogInformation(
                        "[EMAIL CONFIRMACAO] Reenvio {Count}/2 concluido para o usuario {UserId}. Limite de reenvios automaticos atingido.",
                        delivery.ReminderCount.Value,
                        issue.UserId);
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
    }
}
