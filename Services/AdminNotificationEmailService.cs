using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Npgsql;

namespace PremierAPI.Services
{
    public class AdminNotificationEmailService
    {
        private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

        private readonly IConfiguration _config;
        private readonly ILogger<AdminNotificationEmailService> _logger;
        private readonly string _connectionString;

        public AdminNotificationEmailService(
            IConfiguration config,
            ILogger<AdminNotificationEmailService> logger)
        {
            _config = config;
            _logger = logger;
            _connectionString = config.GetConnectionString("DefaultConnection") ?? "";
        }

        public Task TrySendNewUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return TrySendUserNotificationAsync(userId, cancellationToken);
        }

        public Task TrySendOrderCreatedAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            return TrySendOrderNotificationAsync(
                orderId,
                "Novo pedido gerado — Premier Host",
                "Novo pedido gerado",
                "Um usuário gerou um novo pedido no site.",
                cancellationToken);
        }

        public Task TrySendOrderPaidAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            return TrySendOrderNotificationAsync(
                orderId,
                "Pagamento confirmado — Premier Host",
                "Pagamento confirmado",
                "O pagamento de um pedido foi confirmado.",
                cancellationToken);
        }

        public Task TrySendOrderCanceledAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            return TrySendOrderNotificationAsync(
                orderId,
                "Pedido cancelado — Premier Host",
                "Pedido cancelado",
                "Um usuário cancelou um pedido no site.",
                cancellationToken);
        }

        private async Task TrySendUserNotificationAsync(Guid userId, CancellationToken cancellationToken)
        {
            try
            {
                await using var db = new NpgsqlConnection(_connectionString);
                var user = await db.QueryFirstOrDefaultAsync<AdminUserNotificationData>(
                    @"SELECT u.id AS UserId,
                             u.name AS Name,
                             u.email AS Email,
                             u.whatsapp AS Whatsapp,
                             u.created_at AS CreatedAt,
                             ref.referral_code AS ReferralCode
                      FROM users u
                      LEFT JOIN users ref ON ref.id = u.referred_by
                      WHERE u.id = @UserId",
                    new { UserId = userId });

                if (user == null)
                {
                    _logger.LogWarning(
                        "[EMAIL ADMIN] Cadastro {UserId} não encontrado para notificação.",
                        userId);
                    return;
                }

                var rows = new List<(string Label, string Value)>
                {
                    ("ID do usuário", user.UserId.ToString()),
                    ("Nome", user.Name),
                    ("E-mail", user.Email),
                    ("WhatsApp", DisplayOrFallback(user.Whatsapp)),
                    ("Código de indicação usado", DisplayOrFallback(user.ReferralCode)),
                    ("Cadastrado em", FormatDateTime(user.CreatedAt))
                };

                await SendMessageAsync(
                    "Novo usuário cadastrado — Premier Host",
                    BuildHtml(
                        "Novo usuário cadastrado",
                        "Um novo usuário concluiu o cadastro no site.",
                        rows),
                    cancellationToken);

                _logger.LogInformation(
                    "[EMAIL ADMIN] Notificação de novo cadastro enviada. Usuario: {UserId}",
                    userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[EMAIL ADMIN] Falha ao enviar notificação de novo cadastro. Usuario: {UserId}",
                    userId);
            }
        }

        private async Task TrySendOrderNotificationAsync(
            Guid orderId,
            string subject,
            string title,
            string introduction,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var db = new NpgsqlConnection(_connectionString);
                var order = await db.QueryFirstOrDefaultAsync<AdminOrderNotificationData>(
                    @"SELECT o.id AS OrderId,
                             o.created_at AS CreatedAt,
                             o.canceled_at AS CanceledAt,
                             o.status AS Status,
                             o.anydesk_id AS AnyDeskId,
                             o.wyd_server_name AS WydServerName,
                             o.period AS Period,
                             o.days AS Days,
                             o.computers AS Computers,
                             o.wyds_per_computer AS WydsPerComputer,
                             o.total_price AS TotalPrice,
                             o.asaas_payment_id AS AsaasPaymentId,
                             o.asaas_pix_qr_code_id AS AsaasPixQrCodeId,
                             u.id AS UserId,
                             u.name AS UserName,
                             u.email AS UserEmail,
                             u.whatsapp AS UserWhatsapp
                      FROM orders o
                      INNER JOIN users u ON u.id = o.user_id
                      WHERE o.id = @OrderId",
                    new { OrderId = orderId });

                if (order == null)
                {
                    _logger.LogWarning(
                        "[EMAIL ADMIN] Pedido {OrderId} não encontrado para notificação.",
                        orderId);
                    return;
                }

                var rows = new List<(string Label, string Value)>
                {
                    ("ID do pedido", order.OrderId.ToString()),
                    ("Status", order.Status),
                    ("Cliente", order.UserName),
                    ("ID do usuário", order.UserId.ToString()),
                    ("E-mail", order.UserEmail),
                    ("WhatsApp", DisplayOrFallback(order.UserWhatsapp)),
                    ("ID AnyDesk", DisplayOrFallback(order.AnyDeskId)),
                    ("Servidor WYD", DisplayOrFallback(order.WydServerName)),
                    ("Plano", order.Period),
                    ("Validade", $"{order.Days} dias"),
                    ("Computadores", order.Computers.ToString(BrazilianCulture)),
                    ("Slots por computador", order.WydsPerComputer.ToString(BrazilianCulture)),
                    ("Valor", order.TotalPrice.ToString("C", BrazilianCulture)),
                    ("Pedido criado em", FormatDateTime(order.CreatedAt))
                };

                if (order.CanceledAt.HasValue)
                    rows.Add(("Cancelado em", FormatDateTime(order.CanceledAt.Value)));

                string? paymentReference = !string.IsNullOrWhiteSpace(order.AsaasPaymentId)
                    ? order.AsaasPaymentId
                    : order.AsaasPixQrCodeId;
                if (!string.IsNullOrWhiteSpace(paymentReference))
                    rows.Add(("Referência Asaas", paymentReference));

                await SendMessageAsync(
                    subject,
                    BuildHtml(title, introduction, rows),
                    cancellationToken);

                _logger.LogInformation(
                    "[EMAIL ADMIN] Notificação de pedido enviada. Pedido: {OrderId} | Status: {Status}",
                    orderId,
                    order.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[EMAIL ADMIN] Falha ao enviar notificação de pedido. Pedido: {OrderId}",
                    orderId);
            }
        }

        private async Task SendMessageAsync(
            string subject,
            string html,
            CancellationToken cancellationToken)
        {
            string adminEmail = _config["AdminEmail"]
                ?? throw new InvalidOperationException("E-mail administrativo não configurado.");
            string smtpServer = _config["Smtp:Server"]
                ?? throw new InvalidOperationException("Servidor SMTP não configurado.");
            int smtpPort = _config.GetValue<int>("Smtp:Port");
            string smtpUser = _config["Smtp:User"]
                ?? throw new InvalidOperationException("Usuário SMTP não configurado.");
            string smtpPassword = _config["Smtp:Password"]
                ?? throw new InvalidOperationException("Senha SMTP não configurada.");
            string fromName = _config["Smtp:FromName"] ?? "Premier Host";
            string fromEmail = _config["Smtp:FromEmail"]
                ?? throw new InvalidOperationException("Remetente SMTP não configurado.");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress("Administrador", adminEmail));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = html };

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(smtpUser, smtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            try { await client.DisconnectAsync(true, cancellationToken); }
            catch { }
        }

        private static string BuildHtml(
            string title,
            string introduction,
            IEnumerable<(string Label, string Value)> rows)
        {
            var details = new StringBuilder();
            foreach (var row in rows)
            {
                details.Append("<tr>")
                    .Append("<td style='padding: 8px 10px; border-bottom: 1px solid #e5e7eb; color: #6b7280; font-size: 13px;'>")
                    .Append(WebUtility.HtmlEncode(row.Label))
                    .Append("</td>")
                    .Append("<td style='padding: 8px 10px; border-bottom: 1px solid #e5e7eb; color: #111827; font-size: 13px; font-weight: 600; word-break: break-word;'>")
                    .Append(WebUtility.HtmlEncode(row.Value))
                    .Append("</td>")
                    .Append("</tr>");
            }

            return $@"
                <div style='font-family: Arial, sans-serif; max-width: 640px; margin: 0 auto;'>
                    <h2 style='color: #2563eb; font-size: 22px; margin-bottom: 16px;'>{WebUtility.HtmlEncode(title)}</h2>
                    <p style='font-size: 14px; line-height: 1.5; color: #374151;'>{WebUtility.HtmlEncode(introduction)}</p>
                    <table style='width: 100%; border-collapse: collapse; border: 1px solid #e5e7eb; margin: 22px 0;'>
                        {details}
                    </table>
                    <p style='color: #9ca3af; font-size: 12px; margin: 0;'>Premier Host — Notificação administrativa automática.</p>
                </div>";
        }

        private static string DisplayOrFallback(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Não informado" : value.Trim();
        }

        private static string FormatDateTime(DateTime value)
        {
            return value.ToString("dd/MM/yyyy 'às' HH:mm:ss", BrazilianCulture);
        }

        private sealed class AdminUserNotificationData
        {
            public Guid UserId { get; set; }
            public string Name { get; set; } = "";
            public string Email { get; set; } = "";
            public string? Whatsapp { get; set; }
            public DateTime CreatedAt { get; set; }
            public string? ReferralCode { get; set; }
        }

        private sealed class AdminOrderNotificationData
        {
            public Guid OrderId { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? CanceledAt { get; set; }
            public string Status { get; set; } = "";
            public string? AnyDeskId { get; set; }
            public string? WydServerName { get; set; }
            public string Period { get; set; } = "";
            public int Days { get; set; }
            public int Computers { get; set; }
            public int WydsPerComputer { get; set; }
            public decimal TotalPrice { get; set; }
            public string? AsaasPaymentId { get; set; }
            public string? AsaasPixQrCodeId { get; set; }
            public Guid UserId { get; set; }
            public string UserName { get; set; } = "";
            public string UserEmail { get; set; } = "";
            public string? UserWhatsapp { get; set; }
        }
    }
}
