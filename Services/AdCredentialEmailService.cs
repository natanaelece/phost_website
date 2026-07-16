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
    public class AdCredentialEmailService
    {
        private readonly IConfiguration _config;

        public AdCredentialEmailService(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendAsync(string email, string name, string username, string temporaryPassword, CancellationToken cancellationToken = default)
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
            message.Subject = "Seu acesso foi criado — Premier Host";
            message.Body = new TextPart("html")
            {
                Text = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>Seu acesso foi criado</h2>
                        <p style='font-size: 14px;'>Olá, <strong>{WebUtility.HtmlEncode(name)}</strong>!</p>
                        <p style='font-size: 14px; line-height: 1.5;'>O pagamento foi confirmado e sua conta de acesso já está vinculada ao cadastro da Premier Host.</p>
                        <div style='padding: 14px; border: 1px solid #e5e7eb; border-radius: 8px; margin: 24px 0;'>
                            <p style='margin: 0 0 8px; font-size: 13px;'><strong>Usuário:</strong> {WebUtility.HtmlEncode(username)}</p>
                            <p style='margin: 0; font-size: 13px;'><strong>Senha temporária:</strong> {WebUtility.HtmlEncode(temporaryPassword)}</p>
                        </div>
                        <p style='font-size: 13px; line-height: 1.5;'>Não compartilhe estas credenciais. Recomendamos alterar a senha na área de perfil após o primeiro acesso.</p>
                        <hr style='border: 0; border-top: 1px solid #eeeeee; margin: 24px 0;'>
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Não responda este e-mail.</p>
                    </div>"
            };

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(smtpUser, smtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            // O servidor já aceitou a mensagem; falha ao encerrar a sessão não deve redefinir e reenviar a senha.
            try { await client.DisconnectAsync(true, cancellationToken); }
            catch { }
        }
    }
}
