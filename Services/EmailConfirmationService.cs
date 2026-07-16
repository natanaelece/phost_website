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
    public class EmailConfirmationService
    {
        private readonly IConfiguration _config;

        public EmailConfirmationService(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendAsync(string email, string name, string token, CancellationToken cancellationToken = default)
        {
            string smtpServer = _config["Smtp:Server"] ?? throw new InvalidOperationException("Servidor SMTP não configurado.");
            int smtpPort = _config.GetValue<int>("Smtp:Port");
            string smtpUser = _config["Smtp:User"] ?? throw new InvalidOperationException("Usuário SMTP não configurado.");
            string smtpPassword = _config["Smtp:Password"] ?? throw new InvalidOperationException("Senha SMTP não configurada.");
            string fromName = _config["Smtp:FromName"] ?? "Premier Host";
            string fromEmail = _config["Smtp:FromEmail"] ?? throw new InvalidOperationException("Remetente SMTP não configurado.");
            string baseUrl = (_config["PremierConfig:BaseUrlFront"] ?? "").TrimEnd('/');
            string confirmationUrl = $"{baseUrl}/confirmar?token={Uri.EscapeDataString(token)}";

            string safeName = WebUtility.HtmlEncode(name);
            string safeUrl = WebUtility.HtmlEncode(confirmationUrl);
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(name, email));
            message.Subject = "Confirme seu e-mail — Premier Host";
            message.Body = new TextPart("html")
            {
                Text = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>Confirme seu e-mail — Premier Host</h2>
                        <p style='font-size: 14px;'>Olá, <strong>{safeName}</strong>!</p>
                        <p style='font-size: 14px; line-height: 1.5; margin-bottom: 20px;'>Clique no botão abaixo para confirmar seu e-mail e ativar sua conta:</p>
                        <a href='{safeUrl}' style='display: inline-block; padding: 12px 28px; background-color: #2563eb; color: #ffffff !important; border-radius: 8px; font-weight: bold; text-decoration: none; margin: 14px 0;'>Confirmar e-mail</a>
                        <p style='color: #999999; font-size: 13px; margin-top: 20px;'>Se não criou uma conta, ignore este e-mail.</p>
                        <hr style='border: 0; border-top: 1px solid #eeeeee; margin: 24px 0;'>
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Não responda este e-mail.</p>
                    </div>"
            };

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(smtpUser, smtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            // O servidor já aceitou a mensagem; falha ao encerrar a sessão não deve gerar envio duplicado.
            try { await client.DisconnectAsync(true, cancellationToken); }
            catch { }
        }
    }
}
