using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace PremierAPI.Services
{
    public static class EmailConfirmationFailureSanitizer
    {
        public static string Code(Exception exception) =>
            exception switch
            {
                TimeoutException => "smtp_timeout",
                OperationCanceledException => "smtp_canceled",
                _ => "smtp_failure"
            };

        public static Exception SafeException(Exception exception) =>
            new InvalidOperationException(
                $"Falha sanitizada no envio de confirmação ({Code(exception)}).");
    }

    public interface IEmailConfirmationSender
    {
        Task SendAsync(
            string email,
            string name,
            string token,
            CancellationToken cancellationToken = default);

        Task SendConfirmedAsync(
            string email,
            string name,
            CancellationToken cancellationToken = default);
    }

    public class EmailConfirmationService : IEmailConfirmationSender
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailConfirmationService> _logger;

        public EmailConfirmationService(
            IConfiguration config,
            ILogger<EmailConfirmationService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendAsync(string email, string name, string token, CancellationToken cancellationToken = default)
        {
            string baseUrl = (_config["PremierConfig:BaseUrlFront"] ?? "").TrimEnd('/');
            string confirmationUrl = $"{baseUrl}/confirmar?token={Uri.EscapeDataString(token)}";

            string safeName = WebUtility.HtmlEncode(name);
            string safeUrl = WebUtility.HtmlEncode(confirmationUrl);
            await SendMessageAsync(email, name, "Confirme seu e-mail — Premier Host", $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>Confirme seu e-mail — Premier Host</h2>
                        <p style='font-size: 14px;'>Olá, <strong>{safeName}</strong>!</p>
                        <p style='font-size: 14px; line-height: 1.5; margin-bottom: 20px;'>Clique no botão abaixo para confirmar seu e-mail e ativar sua conta:</p>
                        <a href='{safeUrl}' style='display: inline-block; padding: 12px 28px; background-color: #2563eb; color: #ffffff !important; border-radius: 8px; font-weight: bold; text-decoration: none; margin: 14px 0;'>Confirmar e-mail</a>
                        <p style='color: #999999; font-size: 13px; margin-top: 20px;'>Se não criou uma conta, ignore este e-mail.</p>
                        <hr style='border: 0; border-top: 1px solid #eeeeee; margin: 24px 0;'>
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Não responda este e-mail.</p>
                    </div>", cancellationToken);
        }

        public async Task SendConfirmedAsync(string email, string name, CancellationToken cancellationToken = default)
        {
            string safeName = WebUtility.HtmlEncode(name);
            await SendMessageAsync(email, name, "E-mail confirmado — Premier Host", $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>E-mail confirmado!</h2>
                        <p style='font-size: 14px;'>Olá, <strong>{safeName}</strong>!</p>
                        <p style='font-size: 14px; line-height: 1.5;'>Seu e-mail foi confirmado e sua conta Premier Host está ativa.</p>
                        <p style='font-size: 14px; line-height: 1.5;'>Você já pode entrar no painel e acompanhar seus pedidos.</p>
                        <hr style='border: 0; border-top: 1px solid #eeeeee; margin: 24px 0;'>
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Não responda este e-mail.</p>
                    </div>", cancellationToken);
        }

        private async Task SendMessageAsync(string email, string name, string subject, string html, CancellationToken cancellationToken)
        {
            string smtpServer = _config["Smtp:Server"] ?? throw new InvalidOperationException("Servidor SMTP não configurado.");
            int smtpPort = _config.GetValue<int>("Smtp:Port");
            string smtpUser = _config["Smtp:User"] ?? throw new InvalidOperationException("Usuário SMTP não configurado.");
            string smtpPassword = _config["Smtp:Password"] ?? throw new InvalidOperationException("Senha SMTP não configurada.");
            string fromName = _config["Smtp:FromName"] ?? "Premier Host";
            string fromEmail = _config["Smtp:FromEmail"] ?? throw new InvalidOperationException("Remetente SMTP não configurado.");
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(name, email));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = html };

            using var client = new SmtpClient();
            client.Timeout = 60_000;
            await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(smtpUser, smtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            // O servidor já aceitou a mensagem; falha ao encerrar a sessão não deve gerar envio duplicado.
            try { await client.DisconnectAsync(true, cancellationToken); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    EmailConfirmationFailureSanitizer.SafeException(ex),
                    "[EMAIL CONFIRMACAO] Mensagem aceita, mas a sessão SMTP não encerrou normalmente.");
            }
        }
    }
}
