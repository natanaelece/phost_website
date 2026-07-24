//WebhookController.cs
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using System; // Necessário para capturar a Exception
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MimeKit;
using PremierAPI.Services;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/webhook")]
    public class WebhookController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly HttpClient _evoClient;
        private readonly ILogger<WebhookController> _logger;
        private readonly IConfiguration _config;
        private readonly string _adminPhone;
        private readonly string _evoBaseUrl;
        private readonly string _evoInstance;
        private readonly string _evoApikey;
        private readonly bool _dryRun;
        private readonly WhatsAppTemplateService _whatsAppTemplates;
        private readonly AdAccountProvisioningService _adProvisioning;
        private readonly AdminNotificationEmailService _adminNotifications;

        // Variáveis para os tokens de segurança da Asaas
        private readonly string _apiToken;
        private readonly string _sandboxApiToken;

        public WebhookController(
            IConfiguration config,
            ILogger<WebhookController> logger,
            WhatsAppTemplateService whatsAppTemplates,
            AdAccountProvisioningService adProvisioning,
            AdminNotificationEmailService adminNotifications)
        {
            _config = config;
            _whatsAppTemplates = whatsAppTemplates;
            _adProvisioning = adProvisioning;
            _adminNotifications = adminNotifications;
            _connectionString = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _dryRun = config.GetValue<bool>("Evolution:DryRun");
            _adminPhone = config["Evolution:AdminPhone"] ?? "";
            _evoBaseUrl = config["Evolution:BaseUrl"] ?? "";
            _evoInstance = config["Evolution:Instance"] ?? "PHOST";
            _evoApikey = config["Evolution:ApiKey"] ?? "";
            _evoClient = new HttpClient();
            if (!string.IsNullOrWhiteSpace(_evoApikey))
            {
                _evoClient.DefaultRequestHeaders.Add("apikey", _evoApikey);
            }

            // Carregando os tokens configurados no appsettings.json
            _apiToken = config["Asaas:ApiToken"] ?? "";
            _sandboxApiToken = config["Asaas:SandBoxApiToken"] ?? "";
        }

        [HttpPost("asaas")]
        public async Task<IActionResult> ReceiveAsaasWebhook([FromBody] JsonElement payload)
        {
            try
            {
                // VALIDAÇÃO DE SEGURANÇA E AMBIENTE: Verifica o token enviado no cabeçalho
                if (!Request.Headers.TryGetValue("asaas-access-token", out var incomingToken))
                {
                    _logger.LogWarning("[WEBHOOK] Ignorado: Header 'asaas-access-token' ausente.");
                    return Unauthorized(new { success = false, erro = "Token ausente" });
                }

                // Identifica automaticamente se veio da Produção ou Sandbox
                bool isSandbox = incomingToken == _sandboxApiToken;
                bool isProd = incomingToken == _apiToken;

                if (!isSandbox && !isProd)
                {
                    _logger.LogWarning("[WEBHOOK] Ignorado: Token de acesso inválido (Não bate nem com Produção nem com Sandbox).");
                    return Unauthorized(new { success = false, erro = "Token inválido" });
                }

                string envName = isSandbox ? "SANDBOX" : "PRODUÇÃO";
                string eventType = payload.GetProperty("event").GetString() ?? "";

                _logger.LogInformation($"[WEBHOOK RECEBIDO - {envName}] Evento: {eventType}");

                if (eventType == "PAYMENT_RECEIVED" || eventType == "PAYMENT_CONFIRMED")
                {
                    var payment = payload.GetProperty("payment");
                    string paymentId = payment.GetProperty("id").GetString()!;
                    string description = payment.GetProperty("description").GetString() ?? "";
                    string pixQrCodeId = payment.TryGetProperty("pixQrCodeId", out var pixQrCodeIdElement)
                        ? pixQrCodeIdElement.GetString() ?? ""
                        : "";
                    string asaasCustomerId = payment.TryGetProperty("customer", out var customerElement)
                        && customerElement.ValueKind == JsonValueKind.String
                        ? customerElement.GetString() ?? ""
                        : "";

                    using var db = new NpgsqlConnection(_connectionString);

                    int updatedRows;
                    if (!string.IsNullOrWhiteSpace(pixQrCodeId))
                    {
                        // QR estático: o Asaas cria a cobrança apenas depois que o Pix é recebido.
                        updatedRows = await db.ExecuteAsync(
                            @"UPDATE orders
                              SET status = 'pago', asaas_payment_id = @PaymentId,
                                  asaas_customer_id = @CustomerId,
                                  pix_payload = NULL, pix_encoded_image = NULL
                              WHERE asaas_pix_qr_code_id = @QrCodeId
                                AND status <> 'pago'",
                            new { PaymentId = paymentId, CustomerId = asaasCustomerId, QrCodeId = pixQrCodeId });
                    }
                    else
                    {
                        // Compatibilidade com cobranças dinâmicas geradas antes da migração.
                        if (!description.StartsWith("Licença", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation($"[WEBHOOK] Ignorado: pagamento sem QR vinculado e descrição desconhecida ({description})");
                            return Ok(new { success = true, ignored = true });
                        }

                        updatedRows = await db.ExecuteAsync(
                            @"UPDATE orders
                              SET status = 'pago', asaas_customer_id = @CustomerId
                              WHERE asaas_payment_id = @Id AND status <> 'pago'",
                            new { Id = paymentId, CustomerId = asaasCustomerId });
                    }

                    // Busca os dados consolidados do Cliente e do Pedido para os e-mails/whatsapp
                    var orderData = await db.QueryFirstOrDefaultAsync<dynamic>(
                        @"SELECT o.id AS order_id, u.id AS user_id, u.whatsapp, u.email, u.name, o.period, o.days,
                                 o.total_price, o.computers, o.wyds_per_computer,
                                 COALESCE(o.asaas_customer_id, @CustomerId) AS asaas_customer_id
                          FROM orders o JOIN users u ON o.user_id = u.id 
                          WHERE o.asaas_payment_id = @PaymentId
                             OR o.asaas_pix_qr_code_id = @QrCodeId
                          ORDER BY o.created_at DESC
                          LIMIT 1",
                        new { PaymentId = paymentId, QrCodeId = pixQrCodeId, CustomerId = asaasCustomerId });

                    if (orderData == null)
                    {
                        _logger.LogInformation(
                            "[WEBHOOK] Pagamento ignorado por não possuir pedido vinculado. Payment: {PaymentId} | QR: {QrCodeId}",
                            paymentId, pixQrCodeId);
                        return Ok(new { success = true, ignored = true });
                    }

                    if (updatedRows > 0)
                        await _adminNotifications.TrySendOrderPaidAsync((Guid)orderData.order_id);

                    string customerIdToSync = orderData.asaas_customer_id as string ?? asaasCustomerId;
                    if (!string.IsNullOrWhiteSpace(customerIdToSync))
                    {
                        await SyncAsaasCustomerAndDisableNotificationsAsync(
                            customerIdToSync,
                            (Guid)orderData.user_id,
                            (string)orderData.name,
                            (string)orderData.email,
                            orderData.whatsapp as string ?? "",
                            isSandbox);
                    }

                    await _adProvisioning.TryProvisionOrderAsync((Guid)orderData.order_id, HttpContext.RequestAborted);

                    if (updatedRows == 0)
                    {
                        _logger.LogInformation(
                            "[WEBHOOK] Pagamento já processado; cadastro e notificações foram reconciliados novamente. Payment: {PaymentId}",
                            paymentId);
                        return Ok(new { success = true, ignored = true });
                    }

                    await db.ExecuteAsync(@"
                        INSERT INTO product_analytics_events
                            (event_name, session_id, user_id, page_path, properties)
                        VALUES
                            ('payment_received', @SessionId, @UserId, '/webhook/asaas',
                             jsonb_build_object('result', 'confirmed'))",
                        new { SessionId = Guid.NewGuid(), UserId = (Guid)orderData.user_id });

                    string clientPhone = _adminPhone; // Fallback se der erro
                    string clientEmail = "";
                    string clientName = "Cliente";
                    string period = "Licença";
                    int days = 30;
					int computers = 0;
					int wydsPerComputer = 0;
                    decimal totalPrice = 0;

                    if (orderData != null)
                    {
                        clientPhone = string.IsNullOrEmpty((string)orderData.whatsapp) ? _adminPhone : (string)orderData.whatsapp;
                        clientEmail = (string)orderData.email;
                        clientName = (string)orderData.name;
                        period = (string)orderData.period;
                        days = orderData.days ?? 30;
                        totalPrice = orderData.total_price ?? 0;
						computers = orderData.computers;
						wydsPerComputer = orderData.wyds_per_computer;
                    }

                    // Disparos WhatsApp
                    var paymentVariables = WhatsAppTemplateService.BuildPaymentVariables(
                        envName,
                        paymentId,
                        clientName,
                        clientPhone,
                        clientEmail,
                        period,
                        days,
                        totalPrice,
                        computers,
                        wydsPerComputer);

                    string msgClient = await _whatsAppTemplates.RenderAsync(WhatsAppTemplateService.PaymentApprovedClient, paymentVariables);
                    string msgAdmin = await _whatsAppTemplates.RenderAsync(WhatsAppTemplateService.PaymentApprovedAdmin, paymentVariables);

                    await DispararWhatsAppComRetry(clientPhone, msgClient);
                    await DispararWhatsAppComRetry(_adminPhone, msgAdmin);

                    // Disparo do E-mail de Compra Aprovada
                    if (!string.IsNullOrEmpty(clientEmail))
                    {
                        await EnviarEmailCompraAprovada(clientEmail, clientName, period, clientPhone, totalPrice, $"{days}", computers, wydsPerComputer);
                    }
                }

                return Ok(new { success = true });
            }
            catch (Exception ex)
            { 
                // Loga o erro real no console/journalctl antes de retornar o BadRequest
                _logger.LogError($"[WEBHOOK ASAAS ERRO] Falha ao processar: {ex.Message}");
                return BadRequest(); 
            }
        }

        private async Task SyncAsaasCustomerAndDisableNotificationsAsync(
            string customerId,
            Guid userId,
            string name,
            string email,
            string whatsapp,
            bool isSandbox)
        {
            try
            {
                string baseUrl = isSandbox ? _config["Asaas:SandBoxBaseUrl"]! : _config["Asaas:BaseUrl"]!;
                string apiKey = isSandbox
                    ? (_config["Asaas:SandBoxApiKey"] ?? "")
                    : (_config["Asaas:ApiKey"] ?? "");

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                };
                using var http = new HttpClient(handler);
                http.DefaultRequestHeaders.Add("access_token", apiKey);
                http.DefaultRequestHeaders.Add("User-Agent", "Premierhost-BFF/1.0");

                string cleanPhone = Regex.Replace(whatsapp ?? "", @"[^\d]", "");
                var customerUpdate = new
                {
                    name,
                    email,
                    mobilePhone = cleanPhone,
                    externalReference = userId.ToString(),
                    groupName = "PremierHost",
                    notificationDisabled = true
                };
                var customerContent = new StringContent(
                    JsonSerializer.Serialize(customerUpdate), Encoding.UTF8, "application/json");
                var customerResponse = await http.PutAsync($"{baseUrl}/customers/{customerId}", customerContent);
                if (!customerResponse.IsSuccessStatusCode)
                {
                    string body = await customerResponse.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "[ASAAS CUSTOMER SYNC] Falha ao atualizar cliente {CustomerId}. Status: {Status} | Body: {Body}",
                        customerId, customerResponse.StatusCode, body);
                }

                var notificationsResponse = await http.GetAsync($"{baseUrl}/customers/{customerId}/notifications");
                if (!notificationsResponse.IsSuccessStatusCode)
                {
                    string body = await notificationsResponse.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "[ASAAS NOTIFICATIONS] Falha ao listar notificações de {CustomerId}. Status: {Status} | Body: {Body}",
                        customerId, notificationsResponse.StatusCode, body);
                    return;
                }

                string notificationsJson = await notificationsResponse.Content.ReadAsStringAsync();
                using var notificationsDoc = JsonDocument.Parse(notificationsJson);
                var notificationUpdates = notificationsDoc.RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out _))
                    .Select(item => new Dictionary<string, object>
                    {
                        ["id"] = item.GetProperty("id").GetString() ?? "",
                        ["enabled"] = false,
                        ["emailEnabledForProvider"] = false,
                        ["smsEnabledForProvider"] = false,
                        ["emailEnabledForCustomer"] = false,
                        ["smsEnabledForCustomer"] = false,
                        ["phoneCallEnabledForCustomer"] = false,
                        ["whatsappEnabledForCustomer"] = false
                    })
                    .Where(item => !string.IsNullOrWhiteSpace((string)item["id"]))
                    .ToList();

                if (notificationUpdates.Count == 0)
                {
                    _logger.LogWarning("[ASAAS NOTIFICATIONS] Nenhuma notificação encontrada para {CustomerId}.", customerId);
                    return;
                }

                var batchPayload = new { customer = customerId, notifications = notificationUpdates };
                var batchContent = new StringContent(
                    JsonSerializer.Serialize(batchPayload), Encoding.UTF8, "application/json");
                var batchResponse = await http.PutAsync($"{baseUrl}/notifications/batch", batchContent);
                if (!batchResponse.IsSuccessStatusCode)
                {
                    string body = await batchResponse.Content.ReadAsStringAsync();
                    _logger.LogError(
                        "[ASAAS NOTIFICATIONS] Falha ao desativar notificações de {CustomerId}. Status: {Status} | Body: {Body}",
                        customerId, batchResponse.StatusCode, body);
                    return;
                }

                _logger.LogInformation(
                    "[ASAAS CUSTOMER SYNC] Cliente {CustomerId} atualizado, incluído no grupo PremierHost e com {Count} notificações desativadas.",
                    customerId, notificationUpdates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ASAAS CUSTOMER SYNC] Erro ao sincronizar {CustomerId} e desativar notificações.", customerId);
            }
        }

        private async Task EnviarEmailCompraAprovada(string email, string name, string plano, string whatsapp, decimal valor, string validade, int computers, int wydsPerComputer)
        {
            try
            {
                string smtpServer = _config["Smtp:Server"]!;
                int smtpPort = _config.GetValue<int>("Smtp:Port");
                string smtpUser = _config["Smtp:User"]!;
                string smtpPassword = _config["Smtp:Password"]!;

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(
                    _config["Smtp:FromName"]!,
                    _config["Smtp:FromEmail"]!));
                message.To.Add(new MailboxAddress(name, email));
                message.Subject = "✅ Compra aprovada! — Premier Host";

				message.Body = new TextPart("html")
				{
                    Text = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>Compra confirmada! — Premier Host</h2>
                        
                        <p style='font-size: 14px;'>Olá, <strong>{name}</strong>! Seu pagamento foi aprovado.</p>
                        
                        <div style='padding: 14px; border: 1px solid #e5e7eb; border-radius: 8px; margin: 24px 0px;'>
							<p style='margin: 0 0 8px 0; font-size: 12px;'><strong>Quantidade:</strong> {computers} PC's / {wydsPerComputer} slot's.</p>
                            <p style='margin: 0 0 8px 0; font-size: 12px;'><strong>Plano:</strong> {plano} ({validade} dias).</p>
                            <p style='margin: 0 0 2px 0; font-size: 12px;'><strong>Valor Pago:</strong> R$ {valor:N2}</p>
                        </div>
                        
                        <p style='font-size: 14px; line-height: 1.5;'>Nossa equipe técnica já foi notificada!<br>Entraremos em contato através do WhatsApp {whatsapp}.</p>
                        
                        <hr style='border: none; border-top: 1px solid #eeeeee; margin: 24px 0px;'>
                        
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Este é um e-mail automático, não o responda.</p>
                    </div>"
				};

                using var client = new SmtpClient();
                await client.ConnectAsync(smtpServer, smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(smtpUser, smtpPassword);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                
                _logger.LogInformation($"[EMAIL SUCESSO] E-mail de compra enviado para {email}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[SMTP ERROR] Falha ao disparar e-mail de compra aprovada para {email}: {ex.Message}");
            }
        }

        private async Task DispararWhatsAppComRetry(string numero, string mensagem, bool isRetry = false)
        {
            if (_dryRun)
            {
                _logger.LogInformation($"[DRY RUN WHATSAPP ativado]\nPARA: {numero}\nMENSAGEM: {mensagem}\n---");
                return;
            }

            var body = new { number = numero, text = mensagem };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            
            var res = await _evoClient.PostAsync($"{_evoBaseUrl}/message/sendText/{_evoInstance}", content);
            var resStr = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning($"[EVOLUTION ERRO] {resStr}");
                
                // Tratar erro do Baileys (Reiniciar e tentar de novo)
                if (resStr.Contains("Connection Closed") || resStr.Contains("Baileys"))
                {
                    _logger.LogWarning("Reiniciando instância PHOST...");
                    await _evoClient.PutAsync($"{_evoBaseUrl}/instance/restart/{_evoInstance}", null);
                    await Task.Delay(5000); // Aguarda a API subir
                    if (!isRetry) await DispararWhatsAppComRetry(numero, mensagem, true);
                    return;
                }

                // Tratar erro de número inválido (Remover ou colocar o 9)
                if (resStr.Contains("invalid number") && !isRetry)
                {
                    if (numero.Length == 13) // Ex: 55 34 9 99187189 (Tem o 9)
                        numero = numero.Remove(4, 1); // Tira o 9
                    else if (numero.Length == 12) // Ex: 55 34 99187189 (Não tem o 9)
                        numero = numero.Insert(4, "9"); // Coloca o 9

                    _logger.LogWarning($"Tentando novamente com o número ajustado: {numero}");
                    await DispararWhatsAppComRetry(numero, mensagem, true);
                }
            }
        }
    }
}

