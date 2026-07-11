// AdminController.cs
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/admin")]
    public class AdminController : ControllerBase
    {
        private readonly string _connString;
        private readonly ILogger<AdminController> _logger;
        private readonly IConfiguration _config;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Attempts, DateTime LockoutEnd)> _ipRateLimits = new();

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _turnstileSecret;

        public AdminController(IConfiguration config, ILogger<AdminController> logger, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _turnstileSecret = config.GetValue<string>("Cloudflare:TurnstileSecretKey") ?? "";
        }

        private async Task<bool> ValidarTurnstile(string? captchaToken)
        {
            if (string.IsNullOrWhiteSpace(captchaToken)) return false;
            try
            {
                var client = _httpClientFactory.CreateClient();
                var postData = new Dictionary<string, string>
                {
                    { "secret", _turnstileSecret },
                    { "response", captchaToken }
                };
                var content = new FormUrlEncodedContent(postData);
                var response = await client.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", content);
                if (!response.IsSuccessStatusCode) return false;
                var result = await response.Content.ReadFromJsonAsync<PremierAPI.Controllers.TurnstileResponse>();
                return result != null && result.Success;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> ValidateAdmin()
        {
            await Task.CompletedTask;
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return false;
            string? envToken = _config["AdminToken"];
            if (string.IsNullOrWhiteSpace(envToken)) return false;
            return token == envToken;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] AdminLoginRequest req)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (_ipRateLimits.TryGetValue(ip, out var limit))
            {
                if (DateTime.UtcNow < limit.LockoutEnd)
                    return StatusCode(403, new { erro = $"Muitas tentativas. Bloqueado até {limit.LockoutEnd.ToLocalTime():HH:mm:ss}." });
            }

            if (string.IsNullOrWhiteSpace(req.TurnstileResponse))
                return BadRequest(new { erro = "Validacao anti-bot obrigatoria (Captcha ausente)." });

            bool isCaptchaValid = await ValidarTurnstile(req.TurnstileResponse);
            if (!isCaptchaValid)
            {
                _logger.LogInformation("[LOGIN ADMIN BLOQUEADO] Falha no Captcha Turnstile para IP: {IP}", ip);
                return BadRequest(new { erro = "Falha na Validacao do Captcha anti-bot." });
            }

            string? envToken = _config["AdminToken"];
            if (string.IsNullOrWhiteSpace(envToken) || req.Token != envToken)
            {
                var newLimit = limit;
                newLimit.Attempts++;
                if (newLimit.Attempts >= 5)
                {
                    newLimit.LockoutEnd = DateTime.UtcNow.AddMinutes(30);
                    _logger.LogWarning($"⚠️ ALERTA DE SEGURANÇA: Múltiplas tentativas de acesso ao Admin! O IP {ip} foi bloqueado por 30 minutos.");
                }
                _ipRateLimits[ip] = newLimit;
                return Unauthorized(new { erro = "Token invalido." });
            }

            // Sucesso, reseta tentativas
            _ipRateLimits.TryRemove(ip, out _);

            return Ok(new { token = envToken, user = new { name = "Administrador", email = _config["AdminEmail"] } });
        }

        public class AdminLoginRequest { 
        public string Token { get; set; } = ""; 
        [System.Text.Json.Serialization.JsonPropertyName("cf-turnstile-response")]
        public string? TurnstileResponse { get; set; }
    }

        [HttpGet("session")]
        public async Task<IActionResult> GetSession()
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Sessão expirada." });
            return Ok(new { user = new { name = "Administrador", email = _config["AdminEmail"] } });
        }

        [HttpGet("dashboard")]
        public async Task<IActionResult> GetDashboard()
        {
            if (!await ValidateAdmin()) { _logger.LogWarning("[ADMIN] Acesso negado ao dashboard."); return Unauthorized(new { erro = "Acesso negado." }); }
            using var db = new NpgsqlConnection(_connString);
            var s = await db.QueryFirstOrDefaultAsync(@"
                SELECT
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago' AND DATE_TRUNC('month', created_at) = DATE_TRUNC('month', NOW())), 0) AS revenue_this_month,
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago' AND DATE_TRUNC('month', created_at) = DATE_TRUNC('month', NOW() - INTERVAL '1 month')), 0) AS revenue_last_month,
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago'), 0) AS total_revenue,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago') AS paid_orders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND (created_at + (days || ' days')::INTERVAL) > NOW()) AS active_licenses,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND (created_at + (days || ' days')::INTERVAL) <= NOW()) AS expired_licenses,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pendente') AS pending_orders,
                    (SELECT COUNT(*) FROM users) AS total_users,
                    (SELECT COUNT(*) FROM users WHERE DATE_TRUNC('month', created_at) = DATE_TRUNC('month', NOW())) AS new_users_this_month
            ");
            var monthlyRaw = await db.QueryAsync(@"SELECT TO_CHAR(DATE_TRUNC('month', created_at), 'Mon/YY') AS month, COALESCE(SUM(total_price), 0) AS revenue FROM orders WHERE status = 'pago' AND created_at >= DATE_TRUNC('month', NOW() - INTERVAL '5 months') GROUP BY DATE_TRUNC('month', created_at) ORDER BY DATE_TRUNC('month', created_at)");
            var recentRaw = await db.QueryAsync(@"SELECT u.name AS user_name, u.email, u.whatsapp, o.period, o.days, o.computers, o.wyds_per_computer, o.total_price, o.status, o.created_at, (o.created_at + (o.days || ' days')::INTERVAL) AS expires_at, CASE WHEN o.status = 'pago' AND (o.created_at + (o.days || ' days')::INTERVAL) > NOW() THEN true ELSE false END AS is_active FROM orders o JOIN users u ON o.user_id = u.id ORDER BY o.created_at DESC LIMIT 10");
            return Ok(new { stats = new { revenueThisMonth = (decimal)(s?.revenue_this_month ?? 0), revenueLastMonth = (decimal)(s?.revenue_last_month ?? 0), totalRevenue = (decimal)(s?.total_revenue ?? 0), paidOrders = (long)(s?.paid_orders ?? 0), activeLicenses = (long)(s?.active_licenses ?? 0), expiredLicenses = (long)(s?.expired_licenses ?? 0), pendingOrders = (long)(s?.pending_orders ?? 0), totalUsers = (long)(s?.total_users ?? 0), newUsersThisMonth = (long)(s?.new_users_this_month ?? 0) }, monthlyRevenue = monthlyRaw.Select(m => new { month = (string)m.month, revenue = (decimal)m.revenue }), recentOrders = recentRaw.Select(o => new { userName = (string)o.user_name, email = (string)o.email, whatsapp = o.whatsapp as string, period = (string)o.period, days = (int)o.days, computers = (int)o.computers, wydsPerComputer = (int)o.wyds_per_computer, totalPrice = (decimal)o.total_price, status = (string)o.status, createdAt = (DateTime)o.created_at, expiresAt = (DateTime)o.expires_at, isActive = (bool)o.is_active }) });
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetOrders([FromQuery] string status = "all", [FromQuery] int page = 1, [FromQuery] int limit = 20)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var allowed = new HashSet<string> { "all", "active_license", "expired_license", "pago", "pendente", "cancelado", "expirado" };
            if (!allowed.Contains(status)) status = "all";
            if (page < 1) page = 1;
            if (limit < 1 || limit > 100) limit = 20;
            string where = status switch { "active_license" => "WHERE o.status = 'pago' AND (o.created_at + (o.days || ' days')::INTERVAL) > NOW()", "expired_license" => "WHERE o.status = 'pago' AND (o.created_at + (o.days || ' days')::INTERVAL) <= NOW()", "all" => "", _ => $"WHERE o.status = '{status}'" };
            int offset = (page - 1) * limit;
            using var db = new NpgsqlConnection(_connString);
            long total = await db.QueryFirstOrDefaultAsync<long>($"SELECT COUNT(*) FROM orders o JOIN users u ON o.user_id = u.id {where}");
            var raw = await db.QueryAsync($@"SELECT o.id, u.name AS user_name, u.email, u.whatsapp, o.period, o.days, o.computers, o.wyds_per_computer, o.total_price, o.status, o.created_at, o.canceled_at, o.refunded, o.asaas_payment_id, o.delivered, o.delivered_at, (o.created_at + (o.days || ' days')::INTERVAL) AS expires_at, CASE WHEN o.status = 'pago' AND (o.created_at + (o.days || ' days')::INTERVAL) > NOW() THEN true ELSE false END AS is_active FROM orders o JOIN users u ON o.user_id = u.id {where} ORDER BY o.created_at DESC LIMIT @Limit OFFSET @Offset", new { Limit = limit, Offset = offset });
            return Ok(new { total, page, limit, orders = raw.Select(o => new { id = (Guid)o.id, userName = (string)o.user_name, email = (string)o.email, whatsapp = o.whatsapp as string, period = (string)o.period, days = (int)o.days, computers = (int)o.computers, wydsPerComputer = (int)o.wyds_per_computer, totalPrice = (decimal)o.total_price, status = (string)o.status, createdAt = (DateTime)o.created_at, canceledAt = o.canceled_at as DateTime?, refunded = o.refunded as bool? ?? false, expiresAt = (DateTime)o.expires_at, isActive = (bool)o.is_active, asaasPaymentId = o.asaas_payment_id as string, delivered = (bool)o.delivered, deliveredAt = o.delivered_at as DateTime? }) });
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers([FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] string search = "")
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (page < 1) page = 1;
            if (limit < 1 || limit > 100) limit = 20;
            bool hasSearch = !string.IsNullOrWhiteSpace(search);
            int offset = (page - 1) * limit;
            using var db = new NpgsqlConnection(_connString);
            long total = await db.QueryFirstOrDefaultAsync<long>(hasSearch ? "SELECT COUNT(*) FROM users WHERE name ILIKE @Search OR email ILIKE @Search" : "SELECT COUNT(*) FROM users", new { Search = $"%{search}%" });
            string sw = hasSearch ? "WHERE u.name ILIKE @Search OR u.email ILIKE @Search" : "";
            var raw = await db.QueryAsync($@"SELECT u.id, u.name, u.email, u.whatsapp, u.is_active, u.email_confirmed, u.ad_username, u.created_at AS user_created_at, COUNT(o.id) AS total_orders, COALESCE(SUM(CASE WHEN o.status = 'pago' THEN o.total_price ELSE 0 END), 0) AS total_spent, COUNT(CASE WHEN o.status = 'pago' AND (o.created_at + (o.days || ' days')::INTERVAL) > NOW() THEN 1 END) AS active_licenses FROM users u LEFT JOIN orders o ON u.id = o.user_id {sw} GROUP BY u.id, u.name, u.email, u.whatsapp, u.is_active, u.email_confirmed, u.ad_username, u.created_at ORDER BY u.created_at DESC LIMIT @Limit OFFSET @Offset", new { Search = $"%{search}%", Limit = limit, Offset = offset });
            return Ok(new { total, page, limit, users = raw.Select(u => new { id = (Guid)u.id, name = (string)u.name, email = (string)u.email, whatsapp = u.whatsapp as string, isActive = (bool)u.is_active, emailConfirmed = (bool)u.email_confirmed, adUsername = u.ad_username as string, createdAt = (DateTime)u.user_created_at, totalOrders = (long)u.total_orders, totalSpent = (decimal)u.total_spent, activeLicenses = (long)u.active_licenses }) });
        }

        // ==========================================
        // LOCAL USERS CRUD
        // ==========================================
        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] PremierAPI.Controllers.RegisterRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password)) return BadRequest(new { erro = "E-mail e senha sao obrigatorios." });
            using var db = new NpgsqlConnection(_connString);
            var exists = await db.QueryFirstOrDefaultAsync<int>("SELECT 1 FROM users WHERE email = @Email", new { Email = req.Email });
            if (exists == 1) return BadRequest(new { erro = "E-mail ja cadastrado." });

            string hash = BCrypt.Net.BCrypt.HashPassword(req.Password, 12);
            await db.ExecuteAsync("INSERT INTO users (name, email, whatsapp, password_hash, is_active, email_confirmed) VALUES (@Name, @Email, @Whatsapp, @Hash, true, true)", new { Name = req.Name ?? "", Email = req.Email, Whatsapp = req.Whatsapp ?? "", Hash = hash });
            return Ok(new { msg = "Usuario criado com sucesso." });
        }
        [HttpPut("users/{id}")]
        public async Task<IActionResult> UpdateUser(Guid id, [FromBody] PremierAPI.Controllers.RegisterRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            
            if (!string.IsNullOrWhiteSpace(req.Password)) {
                string hash = BCrypt.Net.BCrypt.HashPassword(req.Password, 12);
                await db.ExecuteAsync("UPDATE users SET name=@Name, email=@Email, whatsapp=@Whatsapp, password_hash=@Hash WHERE id=@Id", 
                    new { Name = req.Name, Email = req.Email, Whatsapp = req.Whatsapp, Hash = hash, Id = id });
            } else {
                await db.ExecuteAsync("UPDATE users SET name=@Name, email=@Email, whatsapp=@Whatsapp WHERE id=@Id", 
                    new { Name = req.Name, Email = req.Email, Whatsapp = req.Whatsapp, Id = id });
            }
            
            var adUsername = await db.QueryFirstOrDefaultAsync<string>("SELECT ad_username FROM users WHERE id=@Id", new { Id = id });
            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                var ad = HttpContext.RequestServices.GetService(typeof(PremierAPI.Services.ActiveDirectoryService)) as PremierAPI.Services.ActiveDirectoryService;
                if (ad != null)
                {
                    try {
                        if (!string.IsNullOrWhiteSpace(req.Whatsapp)) await ad.UpdateTelephoneAsync(adUsername, req.Whatsapp);
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "[ADMIN][AD] Falha ao sincronizar UpdateUser para o AD.");
                    }
                }
            }

            return Ok(new { msg = "Usuario atualizado." });
        }

        [HttpPut("users/{id}/confirm-email")]
        public async Task<IActionResult> ConfirmEmailManual(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            int rows = await db.ExecuteAsync("UPDATE users SET email_confirmed = true, email_confirmation_token = NULL WHERE id = @Id", new { Id = id });
            if (rows == 0) return NotFound(new { erro = "Usuario nao encontrado." });
            return Ok(new { msg = "E-mail confirmado manualmente." });
        }

        [HttpPut("users/{id}/active")]
        public async Task<IActionResult> UpdateUserActive(Guid id, [FromBody] UpdateActiveRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var currentUserIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(currentUserIdStr, out Guid currentUserId) && id == currentUserId && !req.IsActive)
                return BadRequest(new { erro = "Voce nao pode inativar sua propria conta." });
            using var db = new NpgsqlConnection(_connString);
            int rows = await db.ExecuteAsync("UPDATE users SET is_active = @IsActive WHERE id = @Id", new { req.IsActive, Id = id });
            if (rows == 0) return NotFound(new { erro = "Usuario nao encontrado." });
            return Ok(new { msg = req.IsActive ? "Usuario ativado." : "Usuario inativado." });
        }

        [HttpPut("users/{id}/ad-link")]
        public async Task<IActionResult> UpdateUserAdLink(Guid id, [FromBody] UpdateAdLinkRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            string? adUsername = string.IsNullOrWhiteSpace(req.AdUsername) ? null : req.AdUsername.Trim();
            bool logonPrepared = true;
            using var db = new NpgsqlConnection(_connString);

            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                int alreadyLinked = await db.QueryFirstOrDefaultAsync<int>(
                    "SELECT COUNT(*) FROM users WHERE id <> @Id AND LOWER(ad_username) = LOWER(@AdUsername)",
                    new { Id = id, AdUsername = adUsername });
                if (alreadyLinked > 0) return BadRequest(new { erro = "Usuario AD ja vinculado a outro cadastro." });

                try
                {
                    logonPrepared = await ad.TryEnsureRequiredLogonComputersAsync(adUsername);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ADMIN][AD] Falha ao preparar logon obrigatorio para {Username}.", adUsername);
                    return BadRequest(new { erro = ex.Message });
                }
            }

            int rows = await db.ExecuteAsync("UPDATE users SET ad_username = @AdUsername WHERE id = @Id", new { AdUsername = adUsername, Id = id });
            if (rows == 0) return NotFound(new { erro = "Usuario nao encontrado." });

            // Sincronizar telephoneNumber no AD com o WhatsApp cadastrado
            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                var whatsapp = await db.QueryFirstOrDefaultAsync<string?>(
                    "SELECT whatsapp FROM users WHERE id = @Id", new { Id = id });
                if (!string.IsNullOrWhiteSpace(whatsapp))
                    await ad.UpdateTelephoneAsync(adUsername, whatsapp);
            }

            return Ok(new
            {
                msg = string.IsNullOrWhiteSpace(adUsername)
                    ? "Vínculo AD removido."
                    : logonPrepared
                        ? "Usuario AD vinculado."
                        : "Usuario AD vinculado. Aviso: a conta LDAP nao tem permissao para atualizar os computadores obrigatorios no AD."
            });
        }


        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            int orders = await db.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM orders WHERE user_id = @Id", new { Id = id });
            if (orders > 0) return BadRequest(new { erro = "Nao e possivel excluir Usuario com pedidos vinculados. Desative a conta se necessario." });
            
            string? adUsername = await db.QueryFirstOrDefaultAsync<string>("SELECT ad_username FROM users WHERE id = @Id", new { Id = id });

            int rows = await db.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = id });
            if (rows == 0) return NotFound(new { erro = "Usuario nao encontrado." });

            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                try
                {
                    var ad = HttpContext.RequestServices.GetService(typeof(PremierAPI.Services.ActiveDirectoryService)) as PremierAPI.Services.ActiveDirectoryService;
                    if (ad != null)
                        await ad.DeleteUserAsync(adUsername);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AD] Falha ao tentar excluir usuário {AdUsername} do AD. Excluído localmente.", adUsername);
                }
            }

            return Ok(new { msg = "Usuario excluido com sucesso." });
        }

        [HttpPut("orders/{id}/delivery")]
        public async Task<IActionResult> UpdateOrderDelivery(Guid id, [FromBody] UpdateOrderDeliveryRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var status = await db.QueryFirstOrDefaultAsync<string>("SELECT status FROM orders WHERE id = @Id", new { Id = id });
            if (status == null) return NotFound(new { erro = "Pedido nao encontrado." });
            if (req.Delivered && status != "pago") return BadRequest(new { erro = "Nao e possivel entregar um pedido que nao esta pago." });

            int rows = await db.ExecuteAsync(
                "UPDATE orders SET delivered = @Delivered, delivered_at = CASE WHEN @Delivered THEN COALESCE(delivered_at, CURRENT_TIMESTAMP) ELSE NULL END WHERE id = @Id",
                new { req.Delivered, Id = id });
            if (rows == 0) return NotFound(new { erro = "Pedido nao encontrado." });
            return Ok(new { msg = req.Delivered ? "Pedido marcado como entregue." : "Pedido marcado como pendente." });
        }

        [HttpPut("orders/{id}/mark-paid")]
        public async Task<IActionResult> MarkOrderPaid(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var order = await db.QueryFirstOrDefaultAsync(
                "SELECT status, asaas_payment_id FROM orders WHERE id = @Id",
                new { Id = id });
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });

            string status = (string)order.status;
            string? paymentId = order.asaas_payment_id as string;
            if (status != "pendente" && status != "expirado") return BadRequest(new { erro = "Apenas pedidos pendentes ou expirados podem ser marcados como pagos." });

            bool asaasCanceled = false;
            if (status == "pendente" && !string.IsNullOrWhiteSpace(paymentId) && !paymentId.StartsWith("MANUAL_"))
            {
                try
                {
                    bool useSandbox = _config.GetValue<bool>("Asaas:UseSandbox");
                    string baseUrl = useSandbox ? _config["Asaas:SandBoxBaseUrl"]! : _config["Asaas:BaseUrl"]!;
                    string apiKey = useSandbox ? (_config["Asaas:SandBoxApiKey"] ?? "") : (_config["Asaas:ApiKey"] ?? "");

                    var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true };
                    using var http = new System.Net.Http.HttpClient(handler);
                    http.DefaultRequestHeaders.Add("access_token", apiKey);
                    http.DefaultRequestHeaders.Add("User-Agent", "Premierhost-BFF/1.0");

                    var response = await http.DeleteAsync($"{baseUrl}/payments/{paymentId}");
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        return BadRequest(new { erro = $"Nao foi possivel cancelar a cobranca pendente na Asaas antes de marcar como pago manualmente: {body}" });
                    }
                    asaasCanceled = true;
                }
                catch (Exception ex)
                {
                    return BadRequest(new { erro = $"Erro ao cancelar cobranca pendente na Asaas: {ex.Message}" });
                }
            }

            await db.ExecuteAsync(
                @"UPDATE orders
                  SET status = 'pago',
                      paid_manually = true,
                      manual_paid_at = CURRENT_TIMESTAMP,
                      canceled_at = NULL,
                      refunded = false,
                      delivered = false,
                      delivered_at = NULL
                  WHERE id = @Id",
                new { Id = id });

            string msg = asaasCanceled
                ? "Pedido marcado como pago manualmente. A cobranca pendente na Asaas foi cancelada para evitar pagamento duplicado."
                : "Pedido marcado como pago manualmente.";
            return Ok(new { msg });
        }
        [HttpPost("orders/manual")]
        public async Task<IActionResult> CreateManualOrder([FromBody] ManualOrderRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            
            string paymentId = "MANUAL_" + Guid.NewGuid().ToString().Substring(0, 8).ToUpper();
            string sqlOrder = @"INSERT INTO orders (user_id, anydesk_id, computers, wyds_per_computer, period, days, total_price, asaas_payment_id, status, paid_manually, manual_paid_at) 
                                VALUES (@UserId, @Anydesk, @Comps, @Wyds, @Period, @Days, @Total, @PayId, 'pago', true, CURRENT_TIMESTAMP)";
            
            await db.ExecuteAsync(sqlOrder, new { 
                UserId = req.UserId, 
                Anydesk = string.IsNullOrWhiteSpace(req.Description) ? "Pedido Manual" : req.Description, 
                Comps = req.Computers, 
                Wyds = req.WydsPerComputer, 
                Period = req.Period.ToLower(), 
                Days = req.Days,
                Total = req.TotalPrice, 
                PayId = paymentId 
            });

            return Ok(new { msg = "Pedido manual criado com sucesso." });
        }

                [HttpDelete("orders/{id}")]
        public async Task<IActionResult> DeleteOrder(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var order = await db.QueryFirstOrDefaultAsync("SELECT status FROM orders WHERE id = @Id", new { Id = id });
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });
            if ((string)order.status != "cancelado") return BadRequest(new { erro = "Apenas pedidos cancelados podem ser excluidos." });

            await db.ExecuteAsync("DELETE FROM orders WHERE id = @Id", new { Id = id });
            return Ok(new { msg = "Pedido excluido permanentemente." });
        }

        [HttpDelete("orders/{id}/cancel")]
        public async Task<IActionResult> CancelOrder(Guid id, [FromQuery] bool refund, [FromServices] IConfiguration config)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            
            var order = await db.QueryFirstOrDefaultAsync(
                "SELECT asaas_payment_id, status, user_id FROM orders WHERE id = @Id", new { Id = id });
            
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });
            
            string paymentId = (string)order.asaas_payment_id;
            string status = (string)order.status;

            if (!string.IsNullOrWhiteSpace(paymentId) && !paymentId.StartsWith("MANUAL_"))
            {
                if (refund)
                {
                    try 
                    {
                        bool useSandbox = config.GetValue<bool>("Asaas:UseSandbox");
                        string baseUrl = useSandbox ? config["Asaas:SandBoxBaseUrl"]! : config["Asaas:BaseUrl"]!;
                        string apiKey = useSandbox ? (config["Asaas:SandBoxApiKey"] ?? "") : (config["Asaas:ApiKey"] ?? "");

                        // Configura bypass de SSL se necessario para testes locais
                        var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true };
                        using var http = new System.Net.Http.HttpClient(handler);
                        http.DefaultRequestHeaders.Add("access_token", apiKey);
                        http.DefaultRequestHeaders.Add("User-Agent", "Premierhost-BFF/1.0");

                        if (status == "pago")
                        {
                            var response = await http.PostAsync($"{baseUrl}/payments/{paymentId}/refund", null);
                            var responseBody = await response.Content.ReadAsStringAsync();
                            
                            if (!response.IsSuccessStatusCode)
                            {
                                string maskedKey = !string.IsNullOrEmpty(apiKey) && apiKey.Length > 10 ? apiKey.Substring(0, 10) + "..." : "VAZIA";
                                return BadRequest(new { erro = $"Asaas Erro: {responseBody} | URL: {baseUrl} | Key: {maskedKey}" });
                            }
                        }
                        else
                        {
                            var response = await http.DeleteAsync($"{baseUrl}/payments/{paymentId}");
                            if (!response.IsSuccessStatusCode)
                            {
                                return BadRequest(new { erro = "Falha ao cancelar cobranca na Asaas." });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return BadRequest(new { erro = $"Erro na integracao Asaas: {ex.Message}" });
                    }
                }
            }

            await db.ExecuteAsync("UPDATE orders SET status = 'cancelado', canceled_at = CURRENT_TIMESTAMP, refunded = @Refund WHERE id = @Id", new { Id = id, Refund = refund });
            
            string finalMessage = refund ? "Pedido cancelado e reembolsado na Asaas com sucesso." : "Pedido cancelado localmente com sucesso (sem reembolso).";
            return Ok(new { msg = finalMessage });
        }

        // ==========================================
        // ACTIVE DIRECTORY
        // ==========================================
        [HttpGet("ad/status")]
        public async Task<IActionResult> GetAdStatus([FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            bool online = await ad.IsOnlineAsync();
            if (online) _logger.LogInformation("[ADMIN][AD] Status consultado: online.");
            else _logger.LogWarning("[ADMIN][AD] Status consultado: offline.");
            return Ok(new { online });
        }

        [HttpGet("ad/users")]
        public async Task<IActionResult> GetAdUsers([FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                var users = await ad.GetUsersAsync();
                _logger.LogInformation("[ADMIN][AD] Usuarios listados: {Count}.", users.Count);
                return Ok(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao listar usuarios.");
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpGet("ad/groups")]
        public async Task<IActionResult> GetAdGroups([FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                var groups = await ad.GetGroupsAsync();
                _logger.LogInformation("[ADMIN][AD] Grupos listados: {Count}.", groups.Count);
                return Ok(groups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao listar grupos.");
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpGet("ad/computers")]
        public async Task<IActionResult> GetAdComputers([FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                var computers = await ad.GetComputersAsync();
                _logger.LogInformation("[ADMIN][AD] Computadores listados: {Count}.", computers.Count);
                return Ok(computers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao listar computadores.");
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPost("ad/users")]
        public async Task<IActionResult> CreateAdUser([FromBody] CreateAdUserRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.CreateUserAsync(req.Username, req.FullName, req.Password, null, req.Whatsapp);
                _logger.LogInformation("[ADMIN][AD] Usuario criado: {Username}.", req.Username);
                return Ok(new { msg = "Usuario criado no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao criar usuario {Username}.", req.Username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpDelete("ad/users/{username}")]
        public async Task<IActionResult> DeleteAdUser(string username, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.DeleteUserAsync(username);
                _logger.LogInformation("[ADMIN][AD] Usuario removido: {Username}.", username);
                return Ok(new { msg = "Usuario removido do AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao remover usuario {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/users/{username}/password")]
        public async Task<IActionResult> SetAdUserPassword(string username, [FromBody] SetAdPasswordRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Password)) return BadRequest(new { erro = "Informe a nova senha." });
            try
            {
                await ad.SetUserPasswordAsync(username, req.Password, req.ForceChangeOnNextLogon);
                _logger.LogInformation("[ADMIN][AD] Senha redefinida para {Username}. Forcar troca: {ForceChange}.", username, req.ForceChangeOnNextLogon);
                
                using var db = new NpgsqlConnection(_connString);
                var wa = await db.QueryFirstOrDefaultAsync<string>("SELECT whatsapp FROM users WHERE ad_username = @Username", new { Username = username });
                if (!string.IsNullOrWhiteSpace(wa))
                {
                    await ad.UpdateTelephoneAsync(username, wa);
                }

                return Ok(new { msg = req.ForceChangeOnNextLogon ? "Senha redefinida. O Usuario devera alterar no proximo logon." : "Senha redefinida." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao redefinir senha de {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }
        [HttpGet("ad/users/{username}")]
        public async Task<IActionResult> GetAdUser(string username, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            var user = await ad.GetUserDetailsAsync(username);
            if (user == null) return NotFound(new { erro = "Usuario nao encontrado no AD." });
            return Ok(user);
        }

        [HttpPut("ad/users/{username}")]
        public async Task<IActionResult> UpdateAdUser(string username, [FromBody] UpdateAdUserDetailsRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.UpdateUserDetailsAsync(username, req.FullName, req.Whatsapp, req.Password, req.IsActive, req.PasswordNeverExpires);
                _logger.LogInformation("[ADMIN][AD] Usuario atualizado: {Username}.", username);
                return Ok(new { msg = "Usuario AD atualizado com sucesso." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar usuario {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/users/{username}/expiration")]
        public async Task<IActionResult> SetAdUserExpiration(string username, [FromBody] SetExpirationRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.SetUserExpirationAsync(username, req.ExpiresAt);
                _logger.LogInformation("[ADMIN][AD] Vencimento atualizado para {Username}: {ExpiresAt}.", username, req.ExpiresAt);
                return Ok(new { msg = "Vencimento atualizado." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar vencimento de {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/users/{username}/groups")]
        public async Task<IActionResult> ManageAdUserGroup(string username, [FromBody] ManageGroupRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.ManageUserGroupAsync(username, req.GroupName, req.Add);
                _logger.LogInformation("[ADMIN][AD] Grupo {GroupName} {Action} para {Username}.", req.GroupName, req.Add ? "adicionado" : "removido", username);
                return Ok(new { msg = "Grupos atualizados." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar grupos de {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/users/{username}/computers")]
        public async Task<IActionResult> SetAdUserComputers(string username, [FromBody] SetComputersRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.SetUserComputersAsync(
                    username,
                    req.ComputersStr ?? "",
                    req.AllowAllComputers,
                    req.ComputerGroups);
                _logger.LogInformation("[ADMIN][AD] Acessos atualizados para {Username}: {Computers}.", username, req.ComputersStr);

                // Sincronizar telephoneNumber com o WhatsApp do Usuario vinculado
                using var db = new NpgsqlConnection(_connString);
                var whatsapp = await db.QueryFirstOrDefaultAsync<string?>(
                    "SELECT whatsapp FROM users WHERE LOWER(ad_username) = LOWER(@AdUsername) AND whatsapp IS NOT NULL AND whatsapp <> ''",
                    new { AdUsername = username });
                if (!string.IsNullOrWhiteSpace(whatsapp))
                    await ad.UpdateTelephoneAsync(username, whatsapp);

                return Ok(new { msg = "Acessos atualizados." });
            }
            catch (PremierAPI.Services.ComputerGroupSelectionRequiredException ex)
            {
                _logger.LogWarning(
                    "[ADMIN][AD] Grupo manual solicitado para {Username}, computador {Computer}.",
                    username, ex.ComputerName);
                return Conflict(new
                {
                    requiresGroupSelection = true,
                    computer = ex.ComputerName,
                    suggestedGroup = ex.SuggestedGroup,
                    operation = ex.Operation,
                    erro = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar acessos de {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPost("ad/users/{username}/duplicate")]
        public async Task<IActionResult> DuplicateAdUser(string username, [FromBody] DuplicateAdUserRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.NewUsername)) return BadRequest(new { erro = "Informe o username do novo Usuario." });
            if (string.IsNullOrWhiteSpace(req.NewFullName)) return BadRequest(new { erro = "Informe o nome completo do novo Usuario." });
            if (string.IsNullOrWhiteSpace(req.Password)) return BadRequest(new { erro = "Informe a senha do novo Usuario." });
            try
            {
                await ad.DuplicateUserAsync(username, req.NewUsername, req.NewFullName, req.Password, req.Whatsapp);
                _logger.LogInformation("[ADMIN][AD] Usuario {New} duplicado de {Source}.", req.NewUsername, username);
                return Ok(new { msg = $"Usuario '{req.NewUsername}' criado com base em '{username}'." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao duplicar {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/users/{username}/ou")]
        public async Task<IActionResult> MoveAdUserOu(string username, [FromBody] MoveOuRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.MoveUserOuAsync(username, req.ToExpired);
                _logger.LogInformation("[ADMIN][AD] Usuario {Username} movido para {TargetOu}.", username, req.ToExpired ? "USUARIOS_EXPIRADOS" : "USUARIOS");
                return Ok(new { msg = "Usuario movido." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao mover usuario {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
        }
    }


    public class UpdateActiveRequest { public bool IsActive { get; set; } }
    public class UpdateAdUserDetailsRequest { public string FullName { get; set; } = ""; public string? Whatsapp { get; set; } public string? Password { get; set; } public bool IsActive { get; set; } public bool PasswordNeverExpires { get; set; } }
    public class UpdateAdLinkRequest { public string? AdUsername { get; set; } }
    public class UpdateOrderDeliveryRequest { public bool Delivered { get; set; } }
    public class CreateAdUserRequest { public string Username { get; set; } = ""; public string FullName { get; set; } = ""; public string Password { get; set; } = ""; public string? Whatsapp { get; set; } }
    public class SetAdPasswordRequest { public string Password { get; set; } = ""; public bool ForceChangeOnNextLogon { get; set; } }
    public class SetExpirationRequest { public DateTime? ExpiresAt { get; set; } }
    public class ManageGroupRequest { public string GroupName { get; set; } = ""; public bool Add { get; set; } }
    public class SetComputersRequest
    {
        public string ComputersStr { get; set; } = "";
        public bool AllowAllComputers { get; set; }
        public Dictionary<string, string>? ComputerGroups { get; set; }
    }
    public class MoveOuRequest { public bool ToExpired { get; set; } }
    public class DuplicateAdUserRequest { public string NewUsername { get; set; } = ""; public string NewFullName { get; set; } = ""; public string Password { get; set; } = ""; public string? Whatsapp { get; set; } }
    
    public class ManualOrderRequest
    {
        public Guid UserId { get; set; }
        public string Period { get; set; } = "";
        public int Days { get; set; }
        public int Computers { get; set; }
        public int WydsPerComputer { get; set; }
        public decimal TotalPrice { get; set; }
        public string Description { get; set; } = "";
    }
}







