using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Dapper;
using System;
using System.Linq;
using Microsoft.AspNetCore.RateLimiting;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/checkout")]
    [EnableRateLimiting("AuthLimiter")]
    public class CheckoutController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CheckoutController> _logger;
        private readonly string _asaasApiKey;
		private readonly string _asaasSandBoxApiKey;
        private readonly string _asaasBaseUrl;
        private readonly bool _debugLogs;
        private readonly string _connString;

        private sealed class ReferralDiscountData
        {
            public Guid? ReferredBy { get; set; }
            public bool UsedReferralDiscount { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private sealed class CouponDiscountData
        {
            public string Code { get; set; } = "";
            public string DiscountType { get; set; } = "";
            public decimal DiscountValue { get; set; }
        }

        public CheckoutController(ILogger<CheckoutController> logger, IConfiguration config)
        {
            _logger = logger;
            _asaasApiKey = config["Asaas:ApiKey"] ?? ""; 
			_asaasSandBoxApiKey = config["Asaas:SandBoxApiKey"] ?? ""; 
            _debugLogs = config.GetValue<bool>("PremierConfig:EnableDebugLogs");
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            
            // FLAG GLOBAL: Asaas Sandbox vs Produção
            bool useSandbox = config.GetValue<bool>("Asaas:UseSandbox");
            _asaasBaseUrl = useSandbox ? config["Asaas:SandBoxBaseUrl"]! : config["Asaas:BaseUrl"]!;
            
            _httpClient = new HttpClient();
			
			string chaveAtiva = useSandbox ? _asaasSandBoxApiKey : _asaasApiKey;
			if (_debugLogs)
			{
				_logger.LogInformation($"[ASAAS INIT] Ambiente: {(useSandbox ? "SANDBOX" : "PRODUÇÃO")} | URL: {_asaasBaseUrl}");
			}
	
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("access_token", chaveAtiva);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Premierhost-BFF/1.0");
        }

        private async Task<bool> ValidateSession(NpgsqlConnection db, Guid userId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return false;

            int valid = await db.QueryFirstOrDefaultAsync<int>(
                @"SELECT 1
                  FROM user_sessions s
                  INNER JOIN users u ON u.id = s.user_id
                  WHERE s.token = @Token
                    AND s.user_id = @UserId
                    AND s.expires_at > @Now
                    AND u.is_active = true",
                new { Token = token, UserId = userId, Now = DateTime.UtcNow });

            return valid == 1;
        }

        [HttpPost("gerarpix")]
        public async Task<IActionResult> GerarPix([FromBody] PedidoRequest pedido)
        {
            if (_debugLogs) _logger.LogInformation($"[CHECKOUT] Iniciado - AnyDesk: {pedido.AnydeskId}");

            using var db = new NpgsqlConnection(_connString);
            await db.OpenAsync();
            
            // 1. Validar Sessão
            if (!await ValidateSession(db, pedido.UserId)) 
                return Unauthorized(new { erro = "Sessão expirada ou inválida." });

            // 2. Validar limites básicos
            if (pedido.Pcs < 1 || pedido.Pcs > 20 || pedido.Wyds < 1 || pedido.Wyds > 8)
                return BadRequest(new { erro = "Valores inválidos." });

            if (pedido.Periodo != "diaria" && pedido.Periodo != "semanal" && pedido.Periodo != "mensal")
                return BadRequest(new { erro = "Período inválido." });

            if (!Regex.IsMatch(pedido.AnydeskId ?? "", @"^\d{6,15}$"))
                return BadRequest(new { erro = "O ID do AnyDesk deve conter de 6 a 15 números." });

            pedido.WydServerName = (pedido.WydServerName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pedido.WydServerName) || pedido.WydServerName.Length > 50)
                return BadRequest(new { erro = "Informe o nome do servidor de WYD com até 50 caracteres.", campo = "wydServerName" });
            if (IsUnsupportedWydServer(pedido.WydServerName))
                return BadRequest(new { erro = "No momento, não atendemos o servidor WYD2.", campo = "wydServerName", codigo = "unsupported_wyd_server" });

            if (pedido.Periodo == "diaria" && pedido.Pcs < 3) 
                pedido.Pcs = 3;
                
            if (pedido.Periodo == "diaria" && pedido.Dias < 3)
                pedido.Dias = 3;
            else if (pedido.Periodo == "semanal")
                pedido.Dias = 7;
            else if (pedido.Periodo == "mensal")
                pedido.Dias = 30;

            // 3. Recalcular Preço no Backend
            decimal bruto = 0;
            decimal descPeriodo = 0;
            decimal descHardware = 0;
            decimal descIndicacao = 0;
            decimal descCupom = 0;
            CouponDiscountData? coupon = null;
            decimal basePadrao = 35 + ((Math.Max(pedido.Wyds, 1) - 1) * 10);

            if (pedido.Periodo == "diaria")
            {
                bruto = ((40M + ((Math.Max(pedido.Wyds, 1) - 1) * 10M)) / 7M) * pedido.Dias * pedido.Pcs;
            }
            else
            {
                if (pedido.Periodo == "semanal") { bruto = basePadrao * pedido.Pcs; }
                else if (pedido.Periodo == "mensal") { bruto = basePadrao * 4 * pedido.Pcs; descPeriodo = bruto * 0.25M; }
                
                descHardware = (pedido.Pcs - 1) * 5; 
                if (descHardware < 0) descHardware = 0;
            }

            if (pedido.UsouDescontoIndicacao && bruto > 0)
            {
                // Verifica se usuário realmente tem direito ao desconto
                var user = await db.QueryFirstOrDefaultAsync<ReferralDiscountData>(
                    @"SELECT referred_by AS ReferredBy,
                             used_referral_discount AS UsedReferralDiscount,
                             created_at AS CreatedAt
                      FROM users
                      WHERE id = @Id",
                    new { Id = pedido.UserId });

                if (user?.ReferredBy != null && !user.UsedReferralDiscount)
                {
                    if (user.CreatedAt.AddHours(12) > DateTime.Now)
                    {
                        descIndicacao = bruto * 0.05M;
                    }
                    else
                    {
                        pedido.UsouDescontoIndicacao = false; // Expirou
                    }
                }
                else
                {
                    pedido.UsouDescontoIndicacao = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(pedido.CodigoCupom) && bruto > 0)
            {
                coupon = await db.QueryFirstOrDefaultAsync<CouponDiscountData>(
                    @"SELECT code AS Code,
                             discount_type AS DiscountType,
                             discount_value AS DiscountValue
                      FROM coupons
                      WHERE code = @Code
                        AND is_active = true
                        AND (max_uses IS NULL OR COALESCE(uses, 0) < max_uses)",
                    new { Code = pedido.CodigoCupom.Trim().ToUpperInvariant() });

                if (coupon == null)
                    return BadRequest(new { erro = "Cupom inválido ou esgotado." });

                descCupom = coupon.DiscountType == "percent"
                    ? bruto * (coupon.DiscountValue / 100M)
                    : coupon.DiscountValue;
            }

            decimal totalCalculado = bruto - descPeriodo - descHardware - descIndicacao - descCupom;
            if (totalCalculado <= 0) totalCalculado = 0.01M; // Prevenção de bug

            // Mantém o mesmo arredondamento comercial exibido pelo painel.
            if (totalCalculado > 50M)
                totalCalculado = Math.Floor(totalCalculado);

            // Forçar o total calculado
            pedido.Total = Math.Round(totalCalculado, 2);

            string pixAddressKey = await GetActivePixAddressKeyAsync();
            if (string.IsNullOrWhiteSpace(pixAddressKey))
                return BadRequest(new { erro = "Nenhuma chave Pix ativa foi encontrada na conta Asaas." });

            Guid orderId = Guid.NewGuid();
            const int pixExpirationSeconds = 900;
            DateTime pixExpiresAt = DateTime.Now.AddSeconds(pixExpirationSeconds);
            var asaasPayload = new
            {
                addressKey = pixAddressKey,
                description = $"Licença ({pedido.Periodo}) - AnyDesk: {pedido.AnydeskId}",
                value = pedido.Total,
                format = "ALL",
                expirationSeconds = pixExpirationSeconds,
                allowsMultiplePayments = false,
                externalReference = orderId.ToString()
            };

            var content = new StringContent(JsonSerializer.Serialize(asaasPayload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_asaasBaseUrl}/pix/qrCodes/static", content);
            var responseString = await response.Content.ReadAsStringAsync();
            
            if (_debugLogs) _logger.LogInformation("[ASAAS PIX QR] Status: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[ASAAS PIX QR] Falha ao gerar QR Code. Status: {Status} | Body: {Body}", response.StatusCode, responseString);
                return BadRequest(new { erro = "O Asaas não conseguiu gerar o QR Code Pix. Verifique se a chave Pix da conta está ativa." });
            }

            using var doc = JsonDocument.Parse(responseString);
            string qrCodeId = doc.RootElement.GetProperty("id").GetString() ?? "";
            string encodedImage = doc.RootElement.GetProperty("encodedImage").GetString() ?? "";
            string pixPayload = doc.RootElement.GetProperty("payload").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(qrCodeId) || string.IsNullOrWhiteSpace(encodedImage) || string.IsNullOrWhiteSpace(pixPayload))
            {
                _logger.LogError("[ASAAS PIX QR] Resposta incompleta ao gerar QR Code.");
                if (!string.IsNullOrWhiteSpace(qrCodeId))
                    await _httpClient.DeleteAsync($"{_asaasBaseUrl}/pix/qrCodes/static/{qrCodeId}");
                return BadRequest(new { erro = "O gateway retornou um QR Code incompleto." });
            }

            try
            {
                using var transaction = await db.BeginTransactionAsync();
                string sqlOrder = @"INSERT INTO orders
                    (id, user_id, anydesk_id, wyd_server_name, computers, wyds_per_computer, period, days, total_price,
                     asaas_pix_qr_code_id, pix_payload, pix_encoded_image, pix_expires_at, status)
                    VALUES
                    (@Id, @UserId, @Anydesk, @WydServerName, @Comps, @Wyds, @Period, @Days, @Total,
                     @QrCodeId, @PixPayload, @EncodedImage, @PixExpiresAt, 'pendente')";
                await db.ExecuteAsync(sqlOrder, new {
                    Id = orderId,
                    UserId = pedido.UserId,
                    Anydesk = pedido.AnydeskId,
                    WydServerName = pedido.WydServerName,
                    Comps = pedido.Pcs,
                    Wyds = pedido.Wyds,
                    Period = pedido.Periodo,
                    Days = pedido.Dias,
                    Total = pedido.Total,
                    QrCodeId = qrCodeId,
                    PixPayload = pixPayload,
                    EncodedImage = encodedImage,
                    PixExpiresAt = pixExpiresAt
                }, transaction);

                if (pedido.UsouDescontoIndicacao)
                {
                    await db.ExecuteAsync(
                        "UPDATE users SET used_referral_discount = true WHERE id = @UserId",
                        new { UserId = pedido.UserId }, transaction);
                }

                if (coupon != null)
                {
                    int couponRows = await db.ExecuteAsync(
                        @"UPDATE coupons
                          SET uses = COALESCE(uses, 0) + 1
                          WHERE code = @Code
                            AND is_active = true
                            AND (max_uses IS NULL OR COALESCE(uses, 0) < max_uses)",
                        new { coupon.Code }, transaction);
                    if (couponRows != 1)
                        throw new InvalidOperationException("O cupom ficou indisponível durante a geração do Pix.");
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await _httpClient.DeleteAsync($"{_asaasBaseUrl}/pix/qrCodes/static/{qrCodeId}");
                throw;
            }

            return Ok(new
            {
                encodedImage,
                payload = pixPayload,
                paymentId = qrCodeId,
                total = pedido.Total,
                expiresAt = pixExpiresAt,
                expiresInSeconds = pixExpirationSeconds
            });
        }

        [HttpPost("validate-server")]
        public IActionResult ValidateWydServer([FromBody] ValidateWydServerRequest request)
        {
            string serverName = (request.ServerName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(serverName) || serverName.Length > 50)
                return BadRequest(new { erro = "Informe o nome do servidor de WYD com até 50 caracteres.", campo = "wydServerName" });
            if (IsUnsupportedWydServer(serverName))
                return BadRequest(new { erro = "No momento, não atendemos o servidor WYD2.", campo = "wydServerName", codigo = "unsupported_wyd_server" });
            return Ok(new { valido = true });
        }

        private static bool IsUnsupportedWydServer(string serverName)
        {
            string normalized = serverName.Trim();
            return normalized.Equals("wyd2", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("wyd 2", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("pending/{userId}")]
        public async Task<IActionResult> GetPendingPix(Guid userId)
        {
            using var db = new NpgsqlConnection(_connString);
            if (!await ValidateSession(db, userId)) return Unauthorized(new { erro = "Sessão inválida." });
            
            var pendingOrder = await db.QueryFirstOrDefaultAsync(
                @"SELECT asaas_payment_id, asaas_pix_qr_code_id, total_price, pix_payload,
                         pix_encoded_image, pix_expires_at, created_at
                  FROM orders 
                  WHERE user_id = @UserId AND status = 'pendente' 
                  ORDER BY created_at DESC LIMIT 1",
                new { UserId = userId });

            if (pendingOrder == null) return NotFound();

            string? staticQrCodeId = pendingOrder.asaas_pix_qr_code_id as string;
            DateTime expiresAt = pendingOrder.pix_expires_at as DateTime?
                ?? ((DateTime)pendingOrder.created_at).AddMinutes(15);
            if (expiresAt <= DateTime.Now)
            {
                string expiredId = staticQrCodeId ?? (string)pendingOrder.asaas_payment_id;
                await db.ExecuteAsync(
                    @"UPDATE orders
                      SET status = 'expirado', pix_payload = NULL, pix_encoded_image = NULL
                      WHERE (asaas_pix_qr_code_id = @Id OR asaas_payment_id = @Id) AND status = 'pendente'",
                    new { Id = expiredId });
                string deleteUrl = staticQrCodeId != null
                    ? $"{_asaasBaseUrl}/pix/qrCodes/static/{staticQrCodeId}"
                    : $"{_asaasBaseUrl}/payments/{expiredId}";
                await _httpClient.DeleteAsync(deleteUrl);
                return NotFound();
            }

            if (staticQrCodeId == null)
            {
                string legacyPaymentId = (string)pendingOrder.asaas_payment_id;
                var qrCodeResponse = await _httpClient.GetAsync($"{_asaasBaseUrl}/payments/{legacyPaymentId}/pixQrCode");
                if (!qrCodeResponse.IsSuccessStatusCode) return NotFound();

                string qrCodeString = await qrCodeResponse.Content.ReadAsStringAsync();
                using var qrDoc = JsonDocument.Parse(qrCodeString);
                return Ok(new
                {
                    paymentId = legacyPaymentId,
                    total = pendingOrder.total_price,
                    encodedImage = qrDoc.RootElement.GetProperty("encodedImage").GetString(),
                    payload = qrDoc.RootElement.GetProperty("payload").GetString(),
                    expiresAt,
                    expiresInSeconds = Math.Max(0, (int)(expiresAt - DateTime.Now).TotalSeconds)
                });
            }

            return Ok(new
            {
                paymentId = staticQrCodeId,
                total = pendingOrder.total_price,
                encodedImage = pendingOrder.pix_encoded_image,
                payload = pendingOrder.pix_payload,
                expiresAt = expiresAt,
                expiresInSeconds = Math.Max(0, (int)(expiresAt - DateTime.Now).TotalSeconds)
            });
        }

        [HttpPost("cancel/{paymentId}")]
        public async Task<IActionResult> CancelPix(string paymentId)
        {
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return Unauthorized();

            using var db = new NpgsqlConnection(_connString);
            
            // Ensure the payment belongs to a valid session
            var order = await db.QueryFirstOrDefaultAsync(
                @"SELECT o.user_id, o.asaas_pix_qr_code_id, o.status,
                         o.pix_expires_at, o.created_at
                  FROM orders o
                  INNER JOIN user_sessions s ON s.user_id = o.user_id
                  WHERE (o.asaas_pix_qr_code_id = @Id OR o.asaas_payment_id = @Id)
                    AND s.token = @Token AND s.expires_at > @Now",
                new { Id = paymentId, Token = token, Now = DateTime.UtcNow });
                
            if (order == null) return Unauthorized(new { erro = "Não autorizado." });
            if ((string)order.status != "pendente")
                return BadRequest(new { erro = "Este Pix não está mais pendente." });

            string? qrCodeId = order.asaas_pix_qr_code_id as string;
            DateTime orderExpiresAt = order.pix_expires_at as DateTime?
                ?? ((DateTime)order.created_at).AddMinutes(15);
            bool expired = orderExpiresAt <= DateTime.Now;
            string deleteUrl = !string.IsNullOrWhiteSpace(qrCodeId)
                ? $"{_asaasBaseUrl}/pix/qrCodes/static/{qrCodeId}"
                : $"{_asaasBaseUrl}/payments/{paymentId}";
            var deleteResponse = await _httpClient.DeleteAsync(deleteUrl);
            if (!deleteResponse.IsSuccessStatusCode && deleteResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                return BadRequest(new { erro = "Não foi possível cancelar o Pix no Asaas." });

            await db.ExecuteAsync(
                @"UPDATE orders
                  SET status = @Status,
                      canceled_at = CASE WHEN @Status = 'cancelado' THEN CURRENT_TIMESTAMP ELSE NULL END,
                      pix_payload = NULL, pix_encoded_image = NULL
                  WHERE (asaas_pix_qr_code_id = @Id OR asaas_payment_id = @Id) AND status = 'pendente'",
                new { Id = paymentId, Status = expired ? "expirado" : "cancelado" });
            
            return Ok(new { success = true });
        }

        private async Task<string> GetActivePixAddressKeyAsync()
        {
            var response = await _httpClient.GetAsync($"{_asaasBaseUrl}/pix/addressKeys?status=ACTIVE&limit=100");
            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("[ASAAS PIX KEY] Falha ao listar chaves ativas. Status: {Status} | Body: {Body}", response.StatusCode, responseBody);
                return string.Empty;
            }

            string responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            foreach (var addressKey in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (addressKey.TryGetProperty("key", out var keyElement))
                    return keyElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        
        [HttpGet("status/{paymentId}")]
        public async Task<IActionResult> CheckStatus(string paymentId)
        {
            using var db = new NpgsqlConnection(_connString);
            string? status = await db.QueryFirstOrDefaultAsync<string>(
                "SELECT status FROM orders WHERE asaas_payment_id = @Id OR asaas_pix_qr_code_id = @Id",
                new { Id = paymentId });
            // O campo known permite que outros webhooks da mesma conta Asaas reconheçam
            // com segurança os QR Codes da PremierHost sem depender da descrição da cobrança.
            // Mantemos "pendente" como fallback para não quebrar clientes antigos deste endpoint.
            return Ok(new { status = status ?? "pendente", known = status != null });
        }
        
        [HttpGet("cupom/{codigo}")]
        public async Task<IActionResult> ValidarCupom(string codigo)
        {
            using var db = new NpgsqlConnection(_connString);
            var cupom = await db.QueryFirstOrDefaultAsync(
                "SELECT code, discount_type, discount_value FROM coupons WHERE code = @Code AND is_active = true AND (max_uses IS NULL OR uses < max_uses)", 
                new { Code = codigo.ToUpper() });
            
            if (cupom == null) return BadRequest(new { erro = "Cupom inválido ou expirado." });
            return Ok(cupom);
        }
    }

    public class PedidoRequest 
    { 
        public Guid UserId { get; set; }
        public decimal Total { get; set; } 
        public string AnydeskId { get; set; } = ""; 
        public string WydServerName { get; set; } = "";
        public string Periodo { get; set; } = ""; 
        public string Nome { get; set; } = ""; 
        public string Whatsapp { get; set; } = ""; 
        public int Pcs { get; set; }
        public int Wyds { get; set; }
        public int Dias { get; set; }
        public bool UsouDescontoIndicacao { get; set; }
        public string CodigoCupom { get; set; } = "";
    }

    public class ValidateWydServerRequest
    {
        public string ServerName { get; set; } = "";
    }
}
