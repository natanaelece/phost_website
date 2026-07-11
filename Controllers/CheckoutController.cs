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
            
            // 1. Validar Sessão
            if (!await ValidateSession(db, pedido.UserId)) 
                return Unauthorized(new { erro = "Sessão expirada ou inválida." });

            // 2. Validar limites básicos
            if (pedido.Pcs < 1 || pedido.Wyds < 1)
                return BadRequest(new { erro = "Valores inválidos." });

            if (pedido.Periodo == "diaria" && pedido.Pcs < 3) 
                pedido.Pcs = 3;
                
            if (pedido.Periodo == "diaria" && pedido.Dias < 3)
                pedido.Dias = 3;

            // 3. Recalcular Preço no Backend
            decimal bruto = 0;
            decimal descPeriodo = 0;
            decimal descHardware = 0;
            decimal descIndicacao = 0;
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

            decimal totalCalculado = bruto - descPeriodo - descHardware - descIndicacao;
            if (totalCalculado <= 0) totalCalculado = 0.01M; // Prevenção de bug

            // Forçar o total calculado
            pedido.Total = Math.Round(totalCalculado, 2);

            string customerId = await GetOrCreateCustomerAsync(pedido.Nome, pedido.Whatsapp);
            if (string.IsNullOrEmpty(customerId)) 
                return BadRequest(new { erro = "Falha de comunicação com o gateway. Verifique os logs." });

            var asaasPayload = new
            {
                customer = customerId,
                billingType = "PIX",
                value = pedido.Total,
                dueDate = System.DateTime.Now.AddDays(1).ToString("yyyy-MM-dd"),
                description = $"Licença ({pedido.Periodo}) - AnyDesk: {pedido.AnydeskId}"
            };

            var content = new StringContent(JsonSerializer.Serialize(asaasPayload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_asaasBaseUrl}/payments", content);
            var responseString = await response.Content.ReadAsStringAsync();
            
            if (_debugLogs) _logger.LogInformation($"[ASAAS PAY] Status: {response.StatusCode} | Body: {responseString}");

            if (!response.IsSuccessStatusCode) return BadRequest(new { erro = "Falha ao gerar cobrança." });

            using var doc = JsonDocument.Parse(responseString);
            string paymentId = doc.RootElement.GetProperty("id").GetString() ?? "";

            // 1. Gravar pedido pendente no banco
            string sqlOrder = @"INSERT INTO orders (user_id, anydesk_id, computers, wyds_per_computer, period, days, total_price, asaas_payment_id, status) 
                                VALUES (@UserId, @Anydesk, @Comps, @Wyds, @Period, @Days, @Total, @PayId, 'pendente')";
            await db.ExecuteAsync(sqlOrder, new { 
                UserId = pedido.UserId, 
                Anydesk = pedido.AnydeskId, 
                Comps = pedido.Pcs, 
                Wyds = pedido.Wyds, 
                Period = pedido.Periodo, 
                Days = pedido.Dias,
                Total = pedido.Total, 
                PayId = paymentId 
            });

            // 2. Trava de Segurança Atômica: Queima a flag de desconto de indicação se usada
            if (pedido.UsouDescontoIndicacao)
            {
                await db.ExecuteAsync("UPDATE users SET used_referral_discount = true WHERE id = @UserId", new { UserId = pedido.UserId });
            }

            // 3. Puxa o QR Code e finaliza
            var qrCodeResponse = await _httpClient.GetAsync($"{_asaasBaseUrl}/payments/{paymentId}/pixQrCode");
            var qrCodeString = await qrCodeResponse.Content.ReadAsStringAsync();

            using var qrDoc = JsonDocument.Parse(qrCodeString);
            return Ok(new
            {
                encodedImage = qrDoc.RootElement.GetProperty("encodedImage").GetString(),
                payload = qrDoc.RootElement.GetProperty("payload").GetString(),
                paymentId = paymentId
            });
        }

        [HttpGet("pending/{userId}")]
        public async Task<IActionResult> GetPendingPix(Guid userId)
        {
            using var db = new NpgsqlConnection(_connString);
            if (!await ValidateSession(db, userId)) return Unauthorized(new { erro = "Sessão inválida." });
            
            // Resolve o erro do F5: Calcula a diferença de tempo (em segundos) direto no PostgreSQL
            var pendingOrder = await db.QueryFirstOrDefaultAsync(
                @"SELECT asaas_payment_id, total_price, 
                         EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP - created_at)) as seconds_passed 
                  FROM orders 
                  WHERE user_id = @UserId AND status = 'pendente' 
                  ORDER BY created_at DESC LIMIT 1",
                new { UserId = userId });

            if (pendingOrder == null) return NotFound();

            double secondsPassed = (double)pendingOrder.seconds_passed;
            
            // Se passou de 15 minutos (900 segundos), expira
            if (secondsPassed >= 900)
            {
                await db.ExecuteAsync("UPDATE orders SET status = 'expirado' WHERE asaas_payment_id = @Id", new { Id = pendingOrder.asaas_payment_id });
                return NotFound();
            }

            var qrCodeResponse = await _httpClient.GetAsync($"{_asaasBaseUrl}/payments/{pendingOrder.asaas_payment_id}/pixQrCode");
            if (!qrCodeResponse.IsSuccessStatusCode) return NotFound();

            var qrCodeString = await qrCodeResponse.Content.ReadAsStringAsync();
            using var qrDoc = JsonDocument.Parse(qrCodeString);

            // Retorna a expiração baseada no tempo que falta
            DateTime expiresAt = DateTime.Now.AddSeconds(900 - secondsPassed);

            return Ok(new
            {
                paymentId = pendingOrder.asaas_payment_id,
                total = pendingOrder.total_price,
                encodedImage = qrDoc.RootElement.GetProperty("encodedImage").GetString(),
                payload = qrDoc.RootElement.GetProperty("payload").GetString(),
                expiresAt = expiresAt
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
                @"SELECT o.user_id FROM orders o
                  INNER JOIN user_sessions s ON s.user_id = o.user_id
                  WHERE o.asaas_payment_id = @Id AND s.token = @Token AND s.expires_at > @Now",
                new { Id = paymentId, Token = token, Now = DateTime.UtcNow });
                
            if (order == null) return Unauthorized(new { erro = "Não autorizado." });

            await db.ExecuteAsync("UPDATE orders SET status = 'cancelado' WHERE asaas_payment_id = @Id", new { Id = paymentId });
            
            // Cancela no gateway também
            await _httpClient.DeleteAsync($"{_asaasBaseUrl}/payments/{paymentId}");
            
            return Ok(new { success = true });
        }

        private async Task<string> GetOrCreateCustomerAsync(string nome, string phone)
        {
            string cleanPhone = Regex.Replace(phone, @"[^\d]", "");
            var searchRes = await _httpClient.GetAsync($"{_asaasBaseUrl}/customers?mobilePhone={cleanPhone}");
            if (searchRes.IsSuccessStatusCode)
            {
                var searchStr = await searchRes.Content.ReadAsStringAsync();
                using var searchDoc = JsonDocument.Parse(searchStr);
                var dataArray = searchDoc.RootElement.GetProperty("data");
                if (dataArray.GetArrayLength() > 0) return dataArray[0].GetProperty("id").GetString() ?? "";
            }

            var newCustomer = new { name = nome, mobilePhone = cleanPhone, groupName = "PremierHost" };
            var content = new StringContent(JsonSerializer.Serialize(newCustomer), Encoding.UTF8, "application/json");
            var createRes = await _httpClient.PostAsync($"{_asaasBaseUrl}/customers", content);
            var createStr = await createRes.Content.ReadAsStringAsync();
            
            if (_debugLogs) _logger.LogInformation($"[ASAAS CUSTOMER CREATE] Body: {createStr}");

            if (createRes.IsSuccessStatusCode)
            {
                using var createDoc = JsonDocument.Parse(createStr);
                return createDoc.RootElement.GetProperty("id").GetString() ?? "";
            }
            return string.Empty;
        }
        
        [HttpGet("status/{paymentId}")]
        public async Task<IActionResult> CheckStatus(string paymentId)
        {
            using var db = new NpgsqlConnection(_connString);
            string? status = await db.QueryFirstOrDefaultAsync<string>("SELECT status FROM orders WHERE asaas_payment_id = @Id", new { Id = paymentId });
            return Ok(new { status = status ?? "pendente" });
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
        public string Periodo { get; set; } = ""; 
        public string Nome { get; set; } = ""; 
        public string Whatsapp { get; set; } = ""; 
        public int Pcs { get; set; }
        public int Wyds { get; set; }
        public int Dias { get; set; }
        public bool UsouDescontoIndicacao { get; set; }
    }
}
