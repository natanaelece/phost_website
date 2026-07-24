// AdminController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PremierAPI.Services;

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
        private readonly AdAccountProvisioningService _adProvisioning;
        private readonly EmailConfirmationService _emailConfirmation;
        private readonly AdPasswordProtectionService _adPasswordProtection;
        private readonly AdminLogStore _adminLogStore;
        private readonly FreeTrialService _freeTrials;
        private readonly AdminMaintenanceService _maintenance;
        private readonly AdminSessionService _adminSessions;
        private readonly AdminTotpService _adminTotp;
        private readonly MetaBusinessEventService _metaBusinessEvents;

        public AdminController(
            IConfiguration config,
            ILogger<AdminController> logger,
            IHttpClientFactory httpClientFactory,
            AdAccountProvisioningService adProvisioning,
            EmailConfirmationService emailConfirmation,
            AdPasswordProtectionService adPasswordProtection,
            AdminLogStore adminLogStore,
            FreeTrialService freeTrials,
            AdminMaintenanceService maintenance,
            AdminSessionService adminSessions,
            AdminTotpService adminTotp,
            MetaBusinessEventService metaBusinessEvents)
        {
            _config = config;
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _turnstileSecret = config.GetValue<string>("Cloudflare:TurnstileSecretKey") ?? "";
            _adProvisioning = adProvisioning;
            _emailConfirmation = emailConfirmation;
            _adPasswordProtection = adPasswordProtection;
            _adminLogStore = adminLogStore;
            _freeTrials = freeTrials;
            _maintenance = maintenance;
            _adminSessions = adminSessions;
            _adminTotp = adminTotp;
            _metaBusinessEvents = metaBusinessEvents;
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
            string? token = Request.Cookies[AdminSessionService.CookieName];
            if (!_adminSessions.TryValidate(token, out var session) || session == null)
                return false;

            if (HttpMethods.IsGet(Request.Method) ||
                HttpMethods.IsHead(Request.Method) ||
                HttpMethods.IsOptions(Request.Method))
            {
                return true;
            }

            string? csrfToken = Request.Headers["X-CSRF-Token"].FirstOrDefault();
            return FixedTimeEquals(csrfToken, session.CsrfToken);
        }

        private static bool FixedTimeEquals(string? provided, string? expected)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
            byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
            byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
        }

        private void IssueAdminSessionCookie(CreatedAdminSession session)
        {
            Response.Cookies.Append(AdminSessionService.CookieName, session.Token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = session.ExpiresAt,
                IsEssential = true
            });
        }

        [HttpPost("login")]
        [EnableRateLimiting("AuthLimiter")]
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
            if (string.IsNullOrWhiteSpace(envToken) || !FixedTimeEquals(req.Token, envToken))
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

            if (_adminTotp.IsEnrolled)
            {
                var challenge = _adminSessions.CreateLoginChallenge(null);
                return Ok(new
                {
                    requiresTwoFactor = true,
                    challengeId = challenge.ChallengeId,
                    expiresAt = challenge.ExpiresAt
                });
            }

            string setupSecret = _adminTotp.GenerateSecret();
            var setupChallenge = _adminSessions.CreateLoginChallenge(setupSecret);
            return Ok(new
            {
                requiresTwoFactorSetup = true,
                challengeId = setupChallenge.ChallengeId,
                expiresAt = setupChallenge.ExpiresAt,
                setupKey = _adminTotp.FormatSecret(setupSecret),
                provisioningUri = _adminTotp.CreateProvisioningUri(setupSecret)
            });
        }

        public class AdminLoginRequest { 
        public string Token { get; set; } = ""; 
        [System.Text.Json.Serialization.JsonPropertyName("cf-turnstile-response")]
        public string? TurnstileResponse { get; set; }
    }

        [HttpPost("login/verify")]
        [EnableRateLimiting("AuthLimiter")]
        public IActionResult VerifyTwoFactor([FromBody] AdminTwoFactorRequest req)
        {
            TotpVerificationResult verification = TotpVerificationResult.Invalid;
            var result = _adminSessions.ValidateLoginChallenge(
                req.ChallengeId,
                challenge =>
                {
                    if (challenge.IsSetup)
                        return _adminTotp.VerifySecret(challenge.PendingSetupSecret!, req.Code);

                    verification = _adminTotp.VerifyEnrolledCode(req.Code);
                    return verification.Success;
                },
                out var validatedChallenge);

            if (result == AdminChallengeValidationResult.Expired)
                return Unauthorized(new { erro = "Desafio expirado ou limite de tentativas excedido. Inicie o login novamente." });
            if (result != AdminChallengeValidationResult.Success || validatedChallenge == null)
                return Unauthorized(new { erro = "Código de autenticação inválido." });

            IReadOnlyList<string> recoveryCodes = Array.Empty<string>();
            if (validatedChallenge.IsSetup)
            {
                try
                {
                    recoveryCodes = _adminTotp.Activate(validatedChallenge.PendingSetupSecret!);
                }
                catch (InvalidOperationException)
                {
                    return Conflict(new { erro = "A autenticação em dois fatores já foi configurada. Inicie o login novamente." });
                }
            }

            if (verification.UsedRecoveryCode)
            {
                _logger.LogWarning(
                    "[ADMIN 2FA] Código de recuperação utilizado. Restam {RemainingCodes} códigos.",
                    verification.RemainingRecoveryCodes);
            }

            var session = _adminSessions.CreateSession();
            IssueAdminSessionCookie(session);
            return Ok(new
            {
                csrfToken = session.CsrfToken,
                expiresAt = session.ExpiresAt,
                recoveryCodes,
                usedRecoveryCode = verification.UsedRecoveryCode,
                remainingRecoveryCodes = verification.RemainingRecoveryCodes,
                user = new { name = "Administrador", email = _config["AdminEmail"] }
            });
        }

        public class AdminTwoFactorRequest
        {
            public string ChallengeId { get; set; } = string.Empty;
            public string Code { get; set; } = string.Empty;
        }

        [HttpGet("session")]
        public async Task<IActionResult> GetSession()
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Sessão expirada." });
            string? token = Request.Cookies[AdminSessionService.CookieName];
            _adminSessions.TryValidate(token, out var session);
            return Ok(new
            {
                csrfToken = session!.CsrfToken,
                expiresAt = session.ExpiresAt,
                user = new { name = "Administrador", email = _config["AdminEmail"] }
            });
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Sessão expirada." });
            string? token = Request.Cookies[AdminSessionService.CookieName];
            _adminSessions.Revoke(token);
            Response.Cookies.Delete(AdminSessionService.CookieName, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
            return Ok(new { msg = "Sessão encerrada." });
        }

        [HttpGet("dashboard")]
        public async Task<IActionResult> GetDashboard([FromQuery] string period = "month", [FromQuery] DateTime? start = null, [FromQuery] DateTime? end = null)
        {
            if (!await ValidateAdmin()) { _logger.LogWarning("[ADMIN] Acesso negado ao dashboard."); return Unauthorized(new { erro = "Acesso negado." }); }

            var now = DateTime.Now;
            var today = now.Date;
            DateTime periodStart;
            DateTime periodEnd;
            string periodLabel;

            switch ((period ?? "month").Trim().ToLowerInvariant())
            {
                case "today":
                    periodStart = today;
                    periodEnd = today.AddDays(1);
                    periodLabel = "Hoje";
                    break;
                case "yesterday":
                    periodStart = today.AddDays(-1);
                    periodEnd = today;
                    periodLabel = "Ontem";
                    break;
                case "7d":
                    periodStart = today.AddDays(-6);
                    periodEnd = today.AddDays(1);
                    periodLabel = "Ultimos 7 dias";
                    break;
                case "30d":
                    periodStart = today.AddDays(-29);
                    periodEnd = today.AddDays(1);
                    periodLabel = "Ultimos 30 dias";
                    break;
                case "last_month":
                    periodStart = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                    periodEnd = new DateTime(today.Year, today.Month, 1);
                    periodLabel = "Mes anterior";
                    break;
                case "quarter":
                    var quarterMonth = ((today.Month - 1) / 3) * 3 + 1;
                    periodStart = new DateTime(today.Year, quarterMonth, 1);
                    periodEnd = periodStart.AddMonths(3);
                    periodLabel = "Trimestre atual";
                    break;
                case "year":
                    periodStart = new DateTime(today.Year, 1, 1);
                    periodEnd = new DateTime(today.Year + 1, 1, 1);
                    periodLabel = "Ano atual";
                    break;
                case "custom":
                    if (!start.HasValue || !end.HasValue) return BadRequest(new { erro = "Informe data inicial e final." });
                    periodStart = start.Value.Date;
                    periodEnd = end.Value.Date.AddDays(1);
                    if (periodEnd <= periodStart) return BadRequest(new { erro = "Periodo personalizado invalido." });
                    periodLabel = "Personalizado";
                    break;
                default:
                    periodStart = new DateTime(today.Year, today.Month, 1);
                    periodEnd = periodStart.AddMonths(1);
                    periodLabel = "Mes atual";
                    period = "month";
                    break;
            }

            var span = periodEnd - periodStart;
            var priorEnd = periodStart;
            var priorStart = periodStart.Subtract(span);
            var bucket = span.TotalDays > 92 ? "month" : "day";
            var labelFormat = bucket == "month" ? "Mon/YY" : "DD/MM";
            var p = new { Start = periodStart, End = periodEnd, PriorStart = priorStart, PriorEnd = priorEnd, Now = now };

            using var db = new NpgsqlConnection(_connString);
            var s = await db.QueryFirstOrDefaultAsync(@"
                SELECT
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago' AND created_at >= @Start AND created_at < @End), 0) AS Revenue,
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago' AND created_at >= @PriorStart AND created_at < @PriorEnd), 0) AS PriorRevenue,
                    COALESCE((SELECT SUM(total_price) FROM orders WHERE status = 'pago'), 0) AS TotalRevenue,
                    COALESCE((SELECT SUM((total_price / GREATEST(days, 1)) * 30) FROM orders WHERE status = 'pago' AND (created_at::date + days + 1) > @Now), 0) AS EstimatedMrr,
                    (SELECT COUNT(*) FROM orders WHERE created_at >= @Start AND created_at < @End) AS OrdersInPeriod,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND created_at >= @Start AND created_at < @End) AS PaidOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND created_at >= @PriorStart AND created_at < @PriorEnd) AS PriorPaidOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pendente' AND created_at >= @Start AND created_at < @End) AS PendingOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'cancelado' AND created_at >= @Start AND created_at < @End) AS CanceledOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'expirado' AND created_at >= @Start AND created_at < @End) AS ExpiredPixOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND paid_manually = true AND created_at >= @Start AND created_at < @End) AS ManualPaidOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND delivered = true AND created_at >= @Start AND created_at < @End) AS DeliveredOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND delivered = false) AS PendingDeliveryOrders,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND (created_at::date + days + 1) > @Now) AS ActiveLicenses,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND (created_at::date + days + 1) <= @Now) AS ExpiredLicenses,
                    (SELECT COUNT(*) FROM orders WHERE status = 'pago' AND (created_at::date + days + 1) > @Now AND (created_at::date + days + 1) <= (@Now + INTERVAL '7 days')) AS ExpiringSoon,
                    (SELECT COUNT(DISTINCT user_id) FROM orders WHERE status = 'pago' AND (created_at::date + days + 1) > @Now) AS ActiveCustomers,
                    (SELECT COUNT(*) FROM users) AS TotalUsers,
                    (SELECT COUNT(*) FROM users WHERE created_at >= @Start AND created_at < @End) AS NewUsers
            ", p);

            var revenueRaw = await db.QueryAsync($@"
                SELECT
                    TO_CHAR(DATE_TRUNC('{bucket}', created_at), '{labelFormat}') AS Label,
                    COALESCE(SUM(CASE WHEN status = 'pago' THEN total_price ELSE 0 END), 0) AS Revenue,
                    COUNT(*) AS Orders
                FROM orders
                WHERE created_at >= @Start AND created_at < @End
                GROUP BY DATE_TRUNC('{bucket}', created_at)
                ORDER BY DATE_TRUNC('{bucket}', created_at)
            ", p);

            var statusRaw = await db.QueryAsync(@"
                SELECT
                    status AS Status,
                    COUNT(*) AS Count,
                    COALESCE(SUM(CASE WHEN status = 'pago' THEN total_price ELSE 0 END), 0) AS Revenue
                FROM orders
                WHERE created_at >= @Start AND created_at < @End
                GROUP BY status
                ORDER BY Count DESC
            ", p);

            var planRaw = await db.QueryAsync(@"
                SELECT
                    COALESCE(period, 'Indefinido') AS Period,
                    COUNT(*) AS Count,
                    COALESCE(SUM(CASE WHEN status = 'pago' THEN total_price ELSE 0 END), 0) AS Revenue,
                    COALESCE(SUM(computers), 0) AS Computers,
                    COALESCE(SUM(computers * wyds_per_computer), 0) AS Slots
                FROM orders
                WHERE created_at >= @Start AND created_at < @End
                GROUP BY COALESCE(period, 'Indefinido')
                ORDER BY Revenue DESC, Count DESC
            ", p);

            var typeRaw = await db.QueryAsync(@"
                SELECT
                    CASE WHEN paid_manually = true OR asaas_payment_id LIKE 'MANUAL_%' THEN 'Manual' ELSE 'Asaas PIX' END AS Type,
                    COUNT(*) AS Count,
                    COALESCE(SUM(CASE WHEN status = 'pago' THEN total_price ELSE 0 END), 0) AS Revenue
                FROM orders
                WHERE created_at >= @Start AND created_at < @End
                GROUP BY CASE WHEN paid_manually = true OR asaas_payment_id LIKE 'MANUAL_%' THEN 'Manual' ELSE 'Asaas PIX' END
                ORDER BY Revenue DESC, Count DESC
            ", p);

            var topRaw = await db.QueryAsync(@"
                SELECT
                    u.name AS UserName,
                    u.email AS Email,
                    u.whatsapp AS Whatsapp,
                    COUNT(o.id) AS Orders,
                    COALESCE(SUM(o.total_price), 0) AS Revenue,
                    MAX(o.created_at) AS LastOrderAt
                FROM orders o
                JOIN users u ON o.user_id = u.id
                WHERE o.status = 'pago' AND o.created_at >= @Start AND o.created_at < @End
                GROUP BY u.id, u.name, u.email, u.whatsapp
                ORDER BY Revenue DESC
                LIMIT 8
            ", p);

            var upcomingRaw = await db.QueryAsync(@"
                SELECT
                    u.name AS UserName,
                    u.email AS Email,
                    u.whatsapp AS Whatsapp,
                    o.period AS Period,
                    o.computers AS Computers,
                    o.wyds_per_computer AS WydsPerComputer,
                    o.total_price AS TotalPrice,
                    (o.created_at::date + o.days)::timestamp AS ExpiresAt
                FROM orders o
                JOIN users u ON o.user_id = u.id
                WHERE o.status = 'pago' AND (o.created_at::date + o.days + 1) > @Now
                ORDER BY ExpiresAt ASC
                LIMIT 8
            ", p);

            var actionRaw = await db.QueryAsync(@"
                SELECT 'Pagamento pendente' AS Type, u.name AS UserName, u.email AS Email, u.whatsapp AS Whatsapp, o.total_price AS TotalPrice, o.created_at AS EventAt
                FROM orders o
                JOIN users u ON o.user_id = u.id
                WHERE o.status = 'pendente'
                UNION ALL
                SELECT 'Entrega pendente' AS Type, u.name AS UserName, u.email AS Email, u.whatsapp AS Whatsapp, o.total_price AS TotalPrice, o.created_at AS EventAt
                FROM orders o
                JOIN users u ON o.user_id = u.id
                WHERE o.status = 'pago' AND o.delivered = false
                UNION ALL
                SELECT 'Licenca vencendo' AS Type, u.name AS UserName, u.email AS Email, u.whatsapp AS Whatsapp, o.total_price AS TotalPrice, (o.created_at::date + o.days)::timestamp AS EventAt
                FROM orders o
                JOIN users u ON o.user_id = u.id
                WHERE o.status = 'pago' AND (o.created_at::date + o.days + 1) > @Now AND (o.created_at::date + o.days + 1) <= (@Now + INTERVAL '7 days')
                ORDER BY EventAt ASC
                LIMIT 10
            ", p);

            var recentRaw = await db.QueryAsync(@"
                SELECT
                    u.name AS UserName,
                    u.email AS Email,
                    u.whatsapp AS Whatsapp,
                    o.period AS Period,
                    o.days AS Days,
                    o.computers AS Computers,
                    o.wyds_per_computer AS WydsPerComputer,
                    o.total_price AS TotalPrice,
                    o.status AS Status,
                    o.created_at AS CreatedAt,
                    (o.created_at::date + o.days)::timestamp AS ExpiresAt,
                    CASE WHEN o.status = 'pago' AND (o.created_at::date + o.days + 1) > @Now THEN true ELSE false END AS IsActive
                FROM orders o
                JOIN users u ON o.user_id = u.id
                ORDER BY o.created_at DESC
                LIMIT 10
            ", p);

            var analyticsRaw = await db.QueryAsync(@"
                SELECT event_name AS EventName,
                       COUNT(*) AS Events,
                       COUNT(DISTINCT session_id) AS Sessions
                FROM product_analytics_events
                WHERE occurred_at >= @Start AND occurred_at < @End
                GROUP BY event_name
                ORDER BY Events DESC
            ", p);

            decimal revenue = (decimal)(s?.revenue ?? 0);
            long ordersInPeriod = (long)(s?.ordersinperiod ?? 0);
            long paidOrders = (long)(s?.paidorders ?? 0);
            decimal averageTicket = paidOrders > 0 ? revenue / paidOrders : 0;
            decimal conversionRate = ordersInPeriod > 0 ? (decimal)paidOrders / ordersInPeriod * 100 : 0;

            return Ok(new
            {
                period = new { key = period, label = periodLabel, start = periodStart, end = periodEnd.AddDays(-1), previousStart = priorStart, previousEnd = priorEnd.AddDays(-1) },
                stats = new
                {
                    revenueThisMonth = revenue,
                    revenueLastMonth = (decimal)(s?.priorrevenue ?? 0),
                    revenue,
                    priorRevenue = (decimal)(s?.priorrevenue ?? 0),
                    totalRevenue = (decimal)(s?.totalrevenue ?? 0),
                    estimatedMrr = (decimal)(s?.estimatedmrr ?? 0),
                    averageTicket,
                    conversionRate,
                    ordersInPeriod,
                    paidOrders,
                    priorPaidOrders = (long)(s?.priorpaidorders ?? 0),
                    pendingOrders = (long)(s?.pendingorders ?? 0),
                    canceledOrders = (long)(s?.canceledorders ?? 0),
                    expiredPixOrders = (long)(s?.expiredpixorders ?? 0),
                    manualPaidOrders = (long)(s?.manualpaidorders ?? 0),
                    deliveredOrders = (long)(s?.deliveredorders ?? 0),
                    pendingDeliveryOrders = (long)(s?.pendingdeliveryorders ?? 0),
                    activeLicenses = (long)(s?.activelicenses ?? 0),
                    expiredLicenses = (long)(s?.expiredlicenses ?? 0),
                    expiringSoon = (long)(s?.expiringsoon ?? 0),
                    activeCustomers = (long)(s?.activecustomers ?? 0),
                    totalUsers = (long)(s?.totalusers ?? 0),
                    newUsersThisMonth = (long)(s?.newusers ?? 0),
                    newUsers = (long)(s?.newusers ?? 0)
                },
                monthlyRevenue = revenueRaw.Select(m => new { month = (string)m.label, revenue = (decimal)m.revenue, orders = (long)m.orders }),
                revenueSeries = revenueRaw.Select(m => new { label = (string)m.label, revenue = (decimal)m.revenue, orders = (long)m.orders }),
                statusBreakdown = statusRaw.Select(x => new { status = (string)x.status, count = (long)x.count, revenue = (decimal)x.revenue }),
                planBreakdown = planRaw.Select(x => new { period = (string)x.period, count = (long)x.count, revenue = (decimal)x.revenue, computers = (long)x.computers, slots = (long)x.slots }),
                orderTypeBreakdown = typeRaw.Select(x => new { type = (string)x.type, count = (long)x.count, revenue = (decimal)x.revenue }),
                analyticsFunnel = analyticsRaw.Select(x => new { eventName = (string)x.eventname, events = (long)x.events, sessions = (long)x.sessions }),
                topCustomers = topRaw.Select(x => new { userName = (string)x.username, email = (string)x.email, whatsapp = x.whatsapp as string, orders = (long)x.orders, revenue = (decimal)x.revenue, lastOrderAt = (DateTime)x.lastorderat }),
                upcomingExpirations = upcomingRaw.Select(x => new { userName = (string)x.username, email = (string)x.email, whatsapp = x.whatsapp as string, period = (string)x.period, computers = (int)x.computers, wydsPerComputer = (int)x.wydspercomputer, totalPrice = (decimal)x.totalprice, expiresAt = (DateTime)x.expiresat }),
                actionQueue = actionRaw.Select(x => new { type = (string)x.type, userName = (string)x.username, email = (string)x.email, whatsapp = x.whatsapp as string, totalPrice = (decimal)x.totalprice, eventAt = (DateTime)x.eventat }),
                recentOrders = recentRaw.Select(o => new { userName = (string)o.username, email = (string)o.email, whatsapp = o.whatsapp as string, period = (string)o.period, days = (int)o.days, computers = (int)o.computers, wydsPerComputer = (int)o.wydspercomputer, totalPrice = (decimal)o.totalprice, status = (string)o.status, createdAt = (DateTime)o.createdat, expiresAt = (DateTime)o.expiresat, isActive = (bool)o.isactive })
            });
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetOrders([FromQuery] string status = "all", [FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] string sortBy = "createdAt", [FromQuery] string sortDir = "desc")
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var allowed = new HashSet<string> { "all", "active_license", "expired_license", "pago", "pendente", "cancelado", "expirado" };
            if (!allowed.Contains(status)) status = "all";
            if (page < 1) page = 1;
            if (limit < 1 || limit > 100) limit = 20;
            var orderColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["customer"] = "u.name",
                ["whatsapp"] = "u.whatsapp",
                ["server"] = "o.wyd_server_name",
                ["period"] = "o.period",
                ["computers"] = "o.computers",
                ["totalPrice"] = "o.total_price",
                ["asaasPaymentId"] = "COALESCE(o.asaas_payment_id, o.asaas_pix_qr_code_id)",
                ["status"] = "o.status",
                ["delivered"] = "o.delivered",
                ["expiresAt"] = "(o.created_at::date + o.days)",
                ["createdAt"] = "o.created_at",
                ["canceledAt"] = "o.canceled_at"
            };
            if (!orderColumns.TryGetValue(sortBy ?? "", out string? orderColumn)) orderColumn = "o.created_at";
            string direction = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            string where = status switch { "active_license" => "WHERE o.status = 'pago' AND (o.created_at::date + o.days + 1) > NOW()", "expired_license" => "WHERE o.status = 'pago' AND (o.created_at::date + o.days + 1) <= NOW()", "all" => "", _ => $"WHERE o.status = '{status}'" };
            int offset = (page - 1) * limit;
            using var db = new NpgsqlConnection(_connString);
            long total = await db.QueryFirstOrDefaultAsync<long>($"SELECT COUNT(*) FROM orders o JOIN users u ON o.user_id = u.id {where}");
            var raw = await db.QueryAsync($@"SELECT o.id, u.name AS user_name, u.email, u.whatsapp, o.wyd_server_name, o.period, o.days, o.computers, o.wyds_per_computer, o.total_price, o.status, o.created_at, o.canceled_at, o.refunded, COALESCE(o.canceled_was_paid, false) AS canceled_was_paid, COALESCE(o.asaas_payment_id, o.asaas_pix_qr_code_id) AS asaas_payment_id, o.paid_manually, o.created_manually, o.manual_paid_at, o.delivered, o.delivered_at, (o.created_at::date + o.days)::timestamp AS expires_at, CASE WHEN o.status = 'pago' AND (o.created_at::date + o.days + 1) > NOW() THEN true ELSE false END AS is_active FROM orders o JOIN users u ON o.user_id = u.id {where} ORDER BY {orderColumn} {direction} NULLS LAST, o.id DESC LIMIT @Limit OFFSET @Offset", new { Limit = limit, Offset = offset });
            return Ok(new { total, page, limit, orders = raw.Select(o => new { id = (Guid)o.id, userName = (string)o.user_name, email = (string)o.email, whatsapp = o.whatsapp as string, wydServerName = o.wyd_server_name as string, period = (string)o.period, days = (int)o.days, computers = (int)o.computers, wydsPerComputer = (int)o.wyds_per_computer, totalPrice = (decimal)o.total_price, status = (string)o.status, createdAt = (DateTime)o.created_at, canceledAt = o.canceled_at as DateTime?, refunded = o.refunded as bool? ?? false, canceledWasPaid = o.canceled_was_paid as bool? ?? false, paidManually = o.paid_manually as bool? ?? false, createdManually = o.created_manually as bool? ?? false, manualPaidAt = o.manual_paid_at as DateTime?, expiresAt = (DateTime)o.expires_at, isActive = (bool)o.is_active, asaasPaymentId = o.asaas_payment_id as string, delivered = (bool)o.delivered, deliveredAt = o.delivered_at as DateTime? }) });
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers([FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] string search = "", [FromQuery] string sortBy = "createdAt", [FromQuery] string sortDir = "desc")
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (page < 1) page = 1;
            if (limit < 1 || limit > 100) limit = 20;
            var orderColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "u.name",
                ["whatsapp"] = "u.whatsapp",
                ["isActive"] = "u.is_active",
                ["adUsername"] = "u.ad_username",
                ["totalOrders"] = "total_orders",
                ["totalSpent"] = "total_spent",
                ["activeLicenses"] = "active_licenses",
                ["createdAt"] = "u.created_at",
                ["emailConfirmed"] = "u.email_confirmed"
            };
            if (!orderColumns.TryGetValue(sortBy ?? "", out string? orderColumn)) orderColumn = "u.created_at";
            string direction = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            bool hasSearch = !string.IsNullOrWhiteSpace(search);
            int offset = (page - 1) * limit;
            using var db = new NpgsqlConnection(_connString);
            long total = await db.QueryFirstOrDefaultAsync<long>(hasSearch ? "SELECT COUNT(*) FROM users WHERE name ILIKE @Search OR email ILIKE @Search" : "SELECT COUNT(*) FROM users", new { Search = $"%{search}%" });
            string sw = hasSearch ? "WHERE u.name ILIKE @Search OR u.email ILIKE @Search" : "";
            var raw = await db.QueryAsync($@"SELECT u.id, u.name, u.email, u.whatsapp, u.is_active, u.email_confirmed, u.ad_username, u.created_at AS user_created_at, u.registration_ip::text AS registration_ip, u.registration_user_agent, u.registration_accept_language, u.registration_country_code, u.registration_referrer_host, u.registration_source, COUNT(o.id) AS total_orders, COALESCE(SUM(CASE WHEN o.status = 'pago' THEN o.total_price ELSE 0 END), 0) AS total_spent, COUNT(CASE WHEN o.status = 'pago' AND (o.created_at::date + o.days + 1) > NOW() THEN 1 END) AS active_licenses FROM users u LEFT JOIN orders o ON u.id = o.user_id {sw} GROUP BY u.id, u.name, u.email, u.whatsapp, u.is_active, u.email_confirmed, u.ad_username, u.created_at, u.registration_ip, u.registration_user_agent, u.registration_accept_language, u.registration_country_code, u.registration_referrer_host, u.registration_source ORDER BY {orderColumn} {direction} NULLS LAST, u.id DESC LIMIT @Limit OFFSET @Offset", new { Search = $"%{search}%", Limit = limit, Offset = offset });
            return Ok(new { total, page, limit, users = raw.Select(u => new { id = (Guid)u.id, name = (string)u.name, email = (string)u.email, whatsapp = u.whatsapp as string, isActive = (bool)u.is_active, emailConfirmed = (bool)u.email_confirmed, adUsername = u.ad_username as string, createdAt = (DateTime)u.user_created_at, registrationIp = u.registration_ip as string, registrationUserAgent = u.registration_user_agent as string, registrationAcceptLanguage = u.registration_accept_language as string, registrationCountryCode = u.registration_country_code as string, registrationReferrerHost = u.registration_referrer_host as string, registrationSource = u.registration_source as string, totalOrders = (long)u.total_orders, totalSpent = (decimal)u.total_spent, activeLicenses = (long)u.active_licenses }) });
        }

        [HttpGet("free-trials")]
        public async Task<IActionResult> GetFreeTrials(
            [FromQuery] string filter = "all", [FromQuery] int page = 1, [FromQuery] int limit = 20,
            [FromQuery] string search = "", [FromQuery] string sortBy = "lastRequestedAt", [FromQuery] string sortDir = "desc")
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var result = await _freeTrials.GetAdminListAsync(filter, page, limit, search, sortBy, sortDir);
            return Ok(new
            {
                result.Total,
                result.Page,
                result.Limit,
                users = result.Users,
                stats = result.Stats
            });
        }

        [HttpPut("free-trials/{id:guid}/{operation}")]
        public async Task<IActionResult> UpdateFreeTrial(Guid id, string operation)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            string actor = _config["AdminEmail"] ?? "admin";
            var result = await _freeTrials.TransitionAsync(id, operation.Trim().ToLowerInvariant(), actor);
            if (result == null) return NotFound(new { erro = "Solicitação de teste não encontrada." });
            if (!result.Success) return Conflict(new { erro = result.Message, status = result.Status });

            _logger.LogInformation(
                "[ADMIN][TESTE GRATIS] Solicitação {RequestId} alterada para {Status}.",
                id, result.Status?.Status);
            if (string.Equals(result.Status?.Status, "liberado", StringComparison.Ordinal))
                await _metaBusinessEvents.TrySendStartTrialAsync(id, HttpContext.RequestAborted);
            return Ok(new { msg = result.Message, status = result.Status });
        }

        [HttpPost("free-trials/users/{userId:guid}/release")]
        public async Task<IActionResult> ReleaseFreeTrialManually(Guid userId)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            string actor = _config["AdminEmail"] ?? "admin";
            var result = await _freeTrials.ReleaseManuallyAsync(userId, actor);
            if (result == null) return NotFound(new { erro = "Usuário não encontrado." });
            if (!result.Success) return Conflict(new { erro = result.Message, status = result.Status });

            _logger.LogInformation(
                "[ADMIN][TESTE GRATIS] Teste liberado manualmente para o usuário {UserId} como solicitação {RequestId}.",
                userId, result.Status?.RequestId);
            if (result.Status?.RequestId is Guid requestId)
                await _metaBusinessEvents.TrySendStartTrialAsync(requestId, HttpContext.RequestAborted);
            return Ok(new { msg = result.Message, status = result.Status });
        }

        [HttpDelete("free-trials/{id:guid}")]
        public async Task<IActionResult> DeleteFreeTrial(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var result = await _freeTrials.DeleteResettableAsync(id);
            if (result == null) return NotFound(new { erro = "Solicitação de teste não encontrada." });
            if (!result.Success) return Conflict(new { erro = result.Message });

            _logger.LogInformation("[ADMIN][TESTE GRATIS] Solicitação {RequestId} excluída e redefinida para nunca solicitou.", id);
            return Ok(new { msg = result.Message });
        }

        [HttpPost("maintenance/{operation}")]
        public async Task<IActionResult> StartMaintenance(string operation)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var result = await _maintenance.StartAsync(operation);
            if (!result.Success)
                return Conflict(new { erro = result.Message, status = result.Status });

            _logger.LogWarning(
                "[ADMIN][MANUTENCAO] Operação {Operation} autorizada no painel como job {JobId}.",
                operation, result.Status?.JobId);
            return Accepted(new { msg = result.Message, status = result.Status });
        }

        [HttpGet("maintenance/{jobId}")]
        public async Task<IActionResult> GetMaintenanceStatus(string jobId)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var status = await _maintenance.GetStatusAsync(jobId);
            return status == null
                ? NotFound(new { erro = "Operação de manutenção não encontrada." })
                : Ok(status);
        }

        // ==========================================
        // WHATSAPP NOTIFICATIONS
        // ==========================================
        [HttpGet("whatsapp/templates")]
        public async Task<IActionResult> GetWhatsAppTemplates([FromServices] PremierAPI.Services.WhatsAppTemplateService templates)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            return Ok(new { templates = await templates.GetTemplatesAsync() });
        }

        [HttpPost("whatsapp/templates")]
        public async Task<IActionResult> CreateWhatsAppTemplate([FromBody] CreateWhatsAppTemplateRequest req, [FromServices] PremierAPI.Services.WhatsAppTemplateService templates)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { erro = "Informe um nome para a mensagem." });
            if (string.IsNullOrWhiteSpace(req.Body)) return BadRequest(new { erro = "A mensagem nao pode ficar vazia." });
            if (req.Body.Length > 4000) return BadRequest(new { erro = "A mensagem deve ter no maximo 4000 caracteres." });

            var created = await templates.CreateTemplateAsync(req.Title, req.Audience, req.TriggerDescription, req.Body);
            if (created == null) return BadRequest(new { erro = "Nao foi possivel criar a mensagem." });

            return Ok(new { msg = "Mensagem criada.", template = created });
        }

        [HttpPut("whatsapp/templates/{key}")]
        public async Task<IActionResult> UpdateWhatsAppTemplate(string key, [FromBody] UpdateWhatsAppTemplateRequest req, [FromServices] PremierAPI.Services.WhatsAppTemplateService templates)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (string.IsNullOrWhiteSpace(req.Body)) return BadRequest(new { erro = "A mensagem nao pode ficar vazia." });
            if (req.Body.Length > 4000) return BadRequest(new { erro = "A mensagem deve ter no maximo 4000 caracteres." });

            var updated = await templates.UpdateTemplateAsync(key, req.Body.TrimEnd());
            if (updated == null) return NotFound(new { erro = "Template nao encontrado." });

            return Ok(new { msg = "Mensagem atualizada.", template = updated });
        }

        [HttpPost("whatsapp/templates/{key}/reset")]
        public async Task<IActionResult> ResetWhatsAppTemplate(string key, [FromServices] PremierAPI.Services.WhatsAppTemplateService templates)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            var updated = await templates.ResetTemplateAsync(key);
            if (updated == null) return NotFound(new { erro = "Template nao encontrado." });

            return Ok(new { msg = "Mensagem restaurada para o padrao.", template = updated });
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
            string protectedPassword = _adPasswordProtection.Protect(req.Password);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            Guid userId = await db.QuerySingleAsync<Guid>(@"
                INSERT INTO users (name, email, whatsapp, password_hash, is_active, email_confirmed, registration_source)
                VALUES (@Name, @Email, @Whatsapp, @Hash, true, true, 'admin')
                RETURNING id",
                new { Name = req.Name ?? "", Email = req.Email, Whatsapp = req.Whatsapp ?? "", Hash = hash }, transaction);
            await db.ExecuteAsync(@"
                INSERT INTO pending_ad_credentials (user_id, protected_password)
                VALUES (@UserId, @ProtectedPassword)",
                new { UserId = userId, ProtectedPassword = protectedPassword }, transaction);
            await transaction.CommitAsync();
            return Ok(new { msg = "Usuario criado com sucesso." });
        }
        [HttpPut("users/{id}")]
        public async Task<IActionResult> UpdateUser(Guid id, [FromBody] PremierAPI.Controllers.RegisterRequest req)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var adUsername = await db.QueryFirstOrDefaultAsync<string>("SELECT ad_username FROM users WHERE id=@Id", new { Id = id });

            if (!string.IsNullOrWhiteSpace(req.Password)) {
                string hash = BCrypt.Net.BCrypt.HashPassword(req.Password, 12);
                await db.OpenAsync();
                await using var transaction = await db.BeginTransactionAsync();
                await db.ExecuteAsync("UPDATE users SET name=@Name, email=@Email, whatsapp=@Whatsapp, password_hash=@Hash WHERE id=@Id",
                    new { Name = req.Name, Email = req.Email, Whatsapp = req.Whatsapp, Hash = hash, Id = id }, transaction);
                if (string.IsNullOrWhiteSpace(adUsername))
                {
                    string protectedPassword = _adPasswordProtection.Protect(req.Password);
                    await db.ExecuteAsync(@"
                        INSERT INTO pending_ad_credentials (user_id, protected_password, updated_at)
                        VALUES (@UserId, @ProtectedPassword, CURRENT_TIMESTAMP)
                        ON CONFLICT (user_id) DO UPDATE
                        SET protected_password = EXCLUDED.protected_password,
                            updated_at = CURRENT_TIMESTAMP",
                        new { UserId = id, ProtectedPassword = protectedPassword }, transaction);
                }
                await transaction.CommitAsync();
            } else {
                await db.ExecuteAsync("UPDATE users SET name=@Name, email=@Email, whatsapp=@Whatsapp WHERE id=@Id", 
                    new { Name = req.Name, Email = req.Email, Whatsapp = req.Whatsapp, Id = id });
            }

            adUsername = await db.QueryFirstOrDefaultAsync<string>("SELECT ad_username FROM users WHERE id=@Id", new { Id = id });
            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                var ad = HttpContext.RequestServices.GetService(typeof(PremierAPI.Services.ActiveDirectoryService)) as PremierAPI.Services.ActiveDirectoryService;
                if (ad != null)
                {
                    try {
                        if (!string.IsNullOrWhiteSpace(req.Password))
                        {
                            await ad.SetUserPasswordAsync(adUsername, req.Password, forceChangeOnNextLogon: false);
                            await db.ExecuteAsync("DELETE FROM pending_ad_credentials WHERE user_id = @Id", new { Id = id });
                        }
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
            await db.OpenAsync(HttpContext.RequestAborted);
            await using var transaction = await db.BeginTransactionAsync(HttpContext.RequestAborted);
            try
            {
                var user = await db.QueryFirstOrDefaultAsync<ManualConfirmationUser>(@"
                    SELECT id AS Id, name AS Name, email AS Email, email_confirmed AS EmailConfirmed
                    FROM users
                    WHERE id = @Id
                    FOR UPDATE", new { Id = id }, transaction);
                if (user == null)
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    return NotFound(new { erro = "Usuario nao encontrado." });
                }
                if (user.EmailConfirmed)
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    return BadRequest(new { erro = "Este e-mail ja foi confirmado." });
                }

                await db.ExecuteAsync(@"
                    UPDATE users
                    SET email_confirmed = true,
                        email_confirmation_token = NULL,
                        email_confirmation_next_send_at = NULL
                    WHERE id = @Id", new { Id = id }, transaction);
                await _emailConfirmation.SendConfirmedAsync(user.Email, user.Name, HttpContext.RequestAborted);
                await transaction.CommitAsync(HttpContext.RequestAborted);
                await _metaBusinessEvents.TrySendCompleteRegistrationAsync(id, HttpContext.RequestAborted);
                return Ok(new { msg = "E-mail confirmado manualmente e cliente notificado." });
            }
            catch (Exception ex)
            {
                if (transaction.Connection != null)
                    await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogError(ex, "[EMAIL CONFIRMACAO] Falha ao confirmar manualmente ou notificar o usuario {UserId}.", id);
                return StatusCode(502, new { erro = "Nao foi possivel confirmar o e-mail e notificar o cliente." });
            }
        }

        [HttpPost("users/{id}/resend-confirmation")]
        public async Task<IActionResult> ResendEmailConfirmation(Guid id, [FromQuery] bool force = false)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            await db.OpenAsync(HttpContext.RequestAborted);
            await using var transaction = await db.BeginTransactionAsync(HttpContext.RequestAborted);
            try
            {
                var user = await db.QueryFirstOrDefaultAsync<ResendConfirmationUser>(@"
                    SELECT id AS Id,
                           name AS Name,
                           email AS Email,
                           email_confirmed AS EmailConfirmed,
                           email_confirmation_token AS EmailConfirmationToken,
                           email_confirmation_last_sent_at AS EmailConfirmationLastSentAt
                    FROM users
                    WHERE id = @Id
                    FOR UPDATE", new { Id = id }, transaction);
                if (user == null)
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    return NotFound(new { erro = "Usuario nao encontrado." });
                }
                if (user.EmailConfirmed)
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    return BadRequest(new { erro = "Este e-mail ja foi confirmado." });
                }

                DateTime localNow = GetEmailConfirmationLocalNow();
                if (!force && user.EmailConfirmationLastSentAt?.Date == localNow.Date)
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    return Conflict(new
                    {
                        requiresConfirmation = true,
                        lastSentAt = user.EmailConfirmationLastSentAt,
                        erro = "Ja houve um envio de confirmacao para este usuario hoje."
                    });
                }

                string token = user.EmailConfirmationToken ?? Guid.NewGuid().ToString();
                if (user.EmailConfirmationToken == null)
                {
                    await db.ExecuteAsync(
                        "UPDATE users SET email_confirmation_token = @Token WHERE id = @Id AND email_confirmed = false",
                        new { Id = id, Token = token }, transaction);
                }
                await _emailConfirmation.SendAsync(user.Email, user.Name, token, HttpContext.RequestAborted);
                await db.ExecuteAsync(@"
                    UPDATE users
                    SET email_confirmation_last_sent_at = @SentAt
                    WHERE id = @Id AND email_confirmed = false",
                    new { Id = id, SentAt = localNow }, transaction);
                await transaction.CommitAsync(HttpContext.RequestAborted);
                return Ok(new { msg = "E-mail de confirmacao reenviado." });
            }
            catch (Exception ex)
            {
                if (transaction.Connection != null)
                    await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogError(ex, "[EMAIL CONFIRMACAO] Falha no reenvio manual para o usuario {UserId}.", id);
                return StatusCode(502, new { erro = "Nao foi possivel reenviar o e-mail de confirmacao." });
            }
        }

        private DateTime GetEmailConfirmationLocalNow()
        {
            string timeZoneId = _config["EmailConfirmationReminders:TimeZone"] ?? "America/Sao_Paulo";
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            }
            catch
            {
                return DateTime.Now;
            }
        }

        [HttpGet("logs")]
        public async Task<IActionResult> GetLogs(
            [FromQuery] string? level = null,
            [FromQuery] string? search = null,
            [FromQuery] int limit = 300,
            [FromQuery] bool usersOnly = false)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            if (usersOnly)
            {
                limit = Math.Clamp(limit, 1, 1_000);
                string term = (search ?? "").Trim();
                await using var db = new NpgsqlConnection(_connString);
                var rows = await db.QueryAsync(@"
                    SELECT
                        e.id,
                        e.event_type,
                        e.occurred_at,
                        e.ip_address::text AS ip_address,
                        e.user_agent,
                        e.accept_language,
                        e.country_code,
                        e.referrer_host,
                        u.id AS user_id,
                        u.name,
                        u.email,
                        u.whatsapp
                    FROM user_activity_events e
                    INNER JOIN users u ON u.id = e.user_id
                    WHERE @Search = ''
                       OR u.name ILIKE @Pattern
                       OR u.email ILIKE @Pattern
                       OR COALESCE(u.whatsapp, '') ILIKE @Pattern
                       OR COALESCE(e.ip_address::text, '') ILIKE @Pattern
                       OR COALESCE(e.user_agent, '') ILIKE @Pattern
                       OR COALESCE(e.accept_language, '') ILIKE @Pattern
                       OR COALESCE(e.country_code, '') ILIKE @Pattern
                       OR COALESCE(e.referrer_host, '') ILIKE @Pattern
                    ORDER BY e.occurred_at DESC, e.id DESC
                    LIMIT @Limit;",
                    new { Search = term, Pattern = $"%{term}%", Limit = limit });

                return Ok(new
                {
                    mode = "users",
                    generatedAt = DateTimeOffset.Now,
                    entries = rows.Select(entry => new
                    {
                        id = (long)entry.id,
                        eventType = (string)entry.event_type,
                        timestamp = (DateTime)entry.occurred_at,
                        userId = (Guid)entry.user_id,
                        name = (string)entry.name,
                        email = (string)entry.email,
                        whatsapp = entry.whatsapp as string,
                        ipAddress = entry.ip_address as string,
                        userAgent = entry.user_agent as string,
                        acceptLanguage = entry.accept_language as string,
                        countryCode = entry.country_code as string,
                        referrerHost = entry.referrer_host as string
                    })
                });
            }

            return Ok(new
            {
                mode = "system",
                generatedAt = DateTimeOffset.Now,
                entries = _adminLogStore.Query(level, search, limit)
            });
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

            int rows = await db.ExecuteAsync(@"
                UPDATE users
                SET ad_username = @AdUsername,
                    ad_credentials_delivered_at = CASE
                        WHEN @AdUsername IS NULL THEN NULL
                        ELSE COALESCE(ad_credentials_delivered_at, CURRENT_TIMESTAMP)
                    END
                WHERE id = @Id", new { AdUsername = adUsername, Id = id });
            if (rows == 0) return NotFound(new { erro = "Usuario nao encontrado." });
            if (!string.IsNullOrWhiteSpace(adUsername))
                await db.ExecuteAsync("DELETE FROM pending_ad_credentials WHERE user_id = @Id", new { Id = id });

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
            var orderState = await db.QueryFirstOrDefaultAsync(
                "SELECT status AS Status, COALESCE(delivered, false) AS Delivered FROM orders WHERE id = @Id",
                new { Id = id });
            if (orderState == null) return NotFound(new { erro = "Pedido não encontrado." });
            if (req.Delivered && (string)orderState.status != "pago") return BadRequest(new { erro = "Não é possível entregar um pedido que não está pago." });
            bool wasDelivered = (bool)orderState.delivered;

            int rows = await db.ExecuteAsync(
                "UPDATE orders SET delivered = @Delivered, delivered_at = CASE WHEN @Delivered THEN COALESCE(delivered_at, CURRENT_TIMESTAMP) ELSE NULL END WHERE id = @Id",
                new { req.Delivered, Id = id });
            if (rows == 0) return NotFound(new { erro = "Pedido nao encontrado." });
            if (req.Delivered && !wasDelivered)
            {
                await db.ExecuteAsync(@"
                    INSERT INTO product_analytics_events
                        (event_name, session_id, user_id, page_path, properties)
                    SELECT 'access_delivered', @SessionId, user_id, '/admin/pedidos',
                           jsonb_build_object('result', 'delivered')
                    FROM orders WHERE id = @Id",
                    new { SessionId = Guid.NewGuid(), Id = id });
            }
            return Ok(new { msg = req.Delivered ? "Pedido marcado como entregue." : "Pedido marcado como pendente." });
        }

        [HttpPut("orders/{id}/mark-paid")]
        public async Task<IActionResult> MarkOrderPaid(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var order = await db.QueryFirstOrDefaultAsync(
                "SELECT status, asaas_payment_id, asaas_pix_qr_code_id FROM orders WHERE id = @Id",
                new { Id = id });
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });

            string status = (string)order.status;
            string? paymentId = order.asaas_payment_id as string;
            string? qrCodeId = order.asaas_pix_qr_code_id as string;
            if (status != "pendente" && status != "expirado") return BadRequest(new { erro = "Apenas pedidos pendentes ou expirados podem ser marcados como pagos." });

            bool asaasCanceled = false;
            if (status == "pendente" &&
                ((!string.IsNullOrWhiteSpace(qrCodeId)) ||
                 (!string.IsNullOrWhiteSpace(paymentId) && !paymentId.StartsWith("MANUAL_"))))
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

                    string cancelUrl = !string.IsNullOrWhiteSpace(qrCodeId)
                        ? $"{baseUrl}/pix/qrCodes/static/{qrCodeId}"
                        : $"{baseUrl}/payments/{paymentId}";
                    var response = await http.DeleteAsync(cancelUrl);
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

            await _adProvisioning.TryProvisionOrderAsync(id, HttpContext.RequestAborted);

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

            string period = (req.Period ?? "").Trim().ToLowerInvariant();
            PricingQuote pricingQuote;
            try { pricingQuote = PricingRules.Calculate(period, req.Computers, req.WydsPerComputer, req.Days); }
            catch (ArgumentException ex) { return BadRequest(new { erro = ex.Message }); }
            int days = pricingQuote.Days;
            string anydesk = Regex.Replace(req.AnyDeskId ?? "", @"\D", "");
            string server = (req.WydServerName ?? "").Trim();
            if (!Regex.IsMatch(anydesk, @"^\d{6,15}$")) return BadRequest(new { erro = "O ID do AnyDesk deve conter de 6 a 15 números." });
            if (string.IsNullOrWhiteSpace(server) || server.Length > 50) return BadRequest(new { erro = "Informe o servidor WYD com até 50 caracteres." });
            if (server.Equals("wyd2", StringComparison.OrdinalIgnoreCase) || server.Equals("wyd 2", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { erro = "No momento, não atendemos o servidor WYD2." });
            if (req.TotalPrice <= 0) return BadRequest(new { erro = "O valor do pedido deve ser maior que zero." });
            int pending = await db.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM orders WHERE user_id = @UserId AND status = 'pendente'", new { req.UserId });
            if (pending > 0) return Conflict(new { erro = "Este cliente já possui um pedido pendente." });

            string sqlOrder = @"INSERT INTO orders (user_id, anydesk_id, wyd_server_name, computers, wyds_per_computer, period, days, total_price, status, paid_manually, created_manually)
                                VALUES (@UserId, @Anydesk, @Server, @Comps, @Wyds, @Period, @Days, @Total, 'pendente', false, true)";
            
            await db.ExecuteAsync(sqlOrder, new { 
                UserId = req.UserId, 
                Anydesk = anydesk,
                Server = server,
                Comps = req.Computers, 
                Wyds = req.WydsPerComputer, 
                Period = period,
                Days = days,
                Total = Math.Round(req.TotalPrice, 2)
            });

            return Ok(new { msg = "Pedido criado como pendente. O cliente já pode gerar o PIX no painel." });
        }

                [HttpDelete("orders/{id}")]
        public async Task<IActionResult> DeleteOrder(Guid id)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            var order = await db.QueryFirstOrDefaultAsync("SELECT status, created_manually, asaas_pix_qr_code_id, asaas_payment_id FROM orders WHERE id = @Id", new { Id = id });
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });
            bool removableManualDraft = (order.created_manually as bool? ?? false)
                && (string)order.status == "pendente"
                && order.asaas_pix_qr_code_id == null
                && order.asaas_payment_id == null;
            if ((string)order.status != "cancelado" && !removableManualDraft) return BadRequest(new { erro = "Apenas pedidos cancelados ou pedidos manuais ainda sem PIX podem ser excluídos." });

            await db.ExecuteAsync("DELETE FROM orders WHERE id = @Id", new { Id = id });
            return Ok(new { msg = "Pedido excluido permanentemente." });
        }

        [HttpDelete("orders/{id}/cancel")]
        public async Task<IActionResult> CancelOrder(Guid id, [FromQuery] bool refund, [FromServices] IConfiguration config)
        {
            if (!await ValidateAdmin()) return Unauthorized(new { erro = "Acesso negado." });
            using var db = new NpgsqlConnection(_connString);
            
            var order = await db.QueryFirstOrDefaultAsync(
                "SELECT asaas_payment_id, asaas_pix_qr_code_id, status, user_id, paid_manually FROM orders WHERE id = @Id", new { Id = id });
            
            if (order == null) return NotFound(new { erro = "Pedido nao encontrado." });
            
            string? paymentId = order.asaas_payment_id as string;
            string? qrCodeId = order.asaas_pix_qr_code_id as string;
            string status = (string)order.status;
            bool paidManually = order.paid_manually as bool? ?? false;
            bool wasPaid = string.Equals(status, "pago", StringComparison.OrdinalIgnoreCase);

            if (refund && paidManually)
                return BadRequest(new { erro = "Este pedido foi pago manualmente e nao possui reembolso pela Asaas." });

            if ((!string.IsNullOrWhiteSpace(paymentId) && !paymentId.StartsWith("MANUAL_")) ||
                !string.IsNullOrWhiteSpace(qrCodeId))
            {
                if (refund || status == "pendente" || status == "expirado")
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
                            if (string.IsNullOrWhiteSpace(paymentId))
                                return BadRequest(new { erro = "O identificador da cobrança paga ainda não foi conciliado pelo webhook do Asaas." });

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
                            string cancelUrl = !string.IsNullOrWhiteSpace(qrCodeId)
                                ? $"{baseUrl}/pix/qrCodes/static/{qrCodeId}"
                                : $"{baseUrl}/payments/{paymentId}";
                            var response = await http.DeleteAsync(cancelUrl);
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

            await db.ExecuteAsync(@"
                UPDATE orders
                SET status = 'cancelado',
                    canceled_at = CURRENT_TIMESTAMP,
                    refunded = @Refund,
                    canceled_was_paid = @WasPaid
                WHERE id = @Id",
                new { Id = id, Refund = refund, WasPaid = wasPaid });
            
            string finalMessage = refund
                ? "Pedido cancelado e reembolsado na Asaas com sucesso."
                : wasPaid
                    ? "Pedido pago cancelado localmente sem reembolso."
                    : "Pedido cancelado com sucesso.";
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

        [HttpPost("ad/groups")]
        public async Task<IActionResult> CreateAdGroup([FromBody] CreateAdGroupRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.CreateGroupAsync(req.Name, req.Description);
                return Ok(new { msg = "Grupo global de segurança criado no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao criar grupo {GroupName}.", req.Name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/groups/{name}")]
        public async Task<IActionResult> UpdateAdGroup(string name, [FromBody] CreateAdGroupRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.UpdateGroupAsync(name, req.Name, req.Description);
                return Ok(new { msg = "Grupo atualizado no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar grupo {GroupName}.", name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpDelete("ad/groups/{name}")]
        public async Task<IActionResult> DeleteAdGroup(string name, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.DeleteGroupAsync(name);
                return Ok(new { msg = "Grupo excluído do AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao excluir grupo {GroupName}.", name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPost("ad/computers")]
        public async Task<IActionResult> CreateAdComputer([FromBody] CreateAdComputerRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.CreateComputerAsync(req.Name, req.Description, req.OperatingSystem, req.IsActive);
                return Ok(new { msg = "Objeto de computador criado no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao criar computador {ComputerName}.", req.Name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/computers/{name}")]
        public async Task<IActionResult> UpdateAdComputer(string name, [FromBody] CreateAdComputerRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.UpdateComputerAsync(name, req.Name, req.Description, req.OperatingSystem, req.IsActive);
                return Ok(new { msg = "Computador atualizado no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar computador {ComputerName}.", name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpDelete("ad/computers/{name}")]
        public async Task<IActionResult> DeleteAdComputer(string name, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.DeleteComputerAsync(name);
                return Ok(new { msg = "Computador excluído do AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao excluir computador {ComputerName}.", name);
                return BadRequest(new { erro = ex.Message });
            }
        }

        [HttpPut("ad/computers/{name}/groups")]
        public async Task<IActionResult> SetAdComputerGroups(string name, [FromBody] SetComputerGroupsRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            if (!await ValidateAdmin()) return Unauthorized();
            try
            {
                await ad.SetComputerGroupsAsync(name, req.Groups);
                _logger.LogInformation("[ADMIN][AD] Grupos do computador {ComputerName} atualizados: {Groups}.", name, string.Join(", ", req.Groups ?? new List<string>()));
                return Ok(new { msg = "Grupos do computador atualizados no AD." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao atualizar grupos do computador {ComputerName}.", name);
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
            try
            {
                var user = await ad.GetUserDetailsAsync(username);
                if (user == null) return NotFound(new { erro = "Usuario nao encontrado no AD." });
                return Ok(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN][AD] Falha ao consultar usuario {Username}.", username);
                return BadRequest(new { erro = ex.Message });
            }
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
                    "[ADMIN][AD] Selecao manual de grupo para {Username}, computador {Computer}. Grupo sugerido: {SuggestedGroup}. Operacao: {Operation}.",
                    username, ex.ComputerName, ex.SuggestedGroup ?? "(nenhum)", ex.Operation);
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


    public class CreateWhatsAppTemplateRequest { public string Title { get; set; } = ""; public string Audience { get; set; } = "Personalizada"; public string TriggerDescription { get; set; } = ""; public string Body { get; set; } = ""; }
    public class ResendConfirmationUser { public Guid Id { get; set; } public string Name { get; set; } = ""; public string Email { get; set; } = ""; public bool EmailConfirmed { get; set; } public string? EmailConfirmationToken { get; set; } public DateTime? EmailConfirmationLastSentAt { get; set; } }
    public class ManualConfirmationUser { public Guid Id { get; set; } public string Name { get; set; } = ""; public string Email { get; set; } = ""; public bool EmailConfirmed { get; set; } }
    public class UpdateWhatsAppTemplateRequest { public string Body { get; set; } = ""; }
    public class UpdateActiveRequest { public bool IsActive { get; set; } }
    public class UpdateAdUserDetailsRequest { public string FullName { get; set; } = ""; public string? Whatsapp { get; set; } public string? Password { get; set; } public bool IsActive { get; set; } public bool PasswordNeverExpires { get; set; } }
    public class UpdateAdLinkRequest { public string? AdUsername { get; set; } }
    public class UpdateOrderDeliveryRequest { public bool Delivered { get; set; } }
    public class CreateAdUserRequest { public string Username { get; set; } = ""; public string FullName { get; set; } = ""; public string Password { get; set; } = ""; public string? Whatsapp { get; set; } }
    public class CreateAdGroupRequest { public string Name { get; set; } = ""; public string? Description { get; set; } }
    public class CreateAdComputerRequest { public string Name { get; set; } = ""; public string? Description { get; set; } public string? OperatingSystem { get; set; } public bool IsActive { get; set; } = true; }
    public class SetComputerGroupsRequest { public List<string> Groups { get; set; } = new(); }
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
        public string AnyDeskId { get; set; } = "";
        public string WydServerName { get; set; } = "";
    }
}
