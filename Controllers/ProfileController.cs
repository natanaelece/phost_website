using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PremierAPI.Services;
namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/profile")]
    public class ProfileController : ControllerBase
    {
        private readonly string _connString;
        private readonly ILogger<ProfileController> _logger;
        private readonly AdPasswordProtectionService _adPasswordProtection;
        private readonly ClientSessionService _clientSessions;
        public ProfileController(
            IConfiguration config,
            ILogger<ProfileController> logger,
            AdPasswordProtectionService adPasswordProtection,
            ClientSessionService clientSessions)
        {
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _adPasswordProtection = adPasswordProtection;
            _clientSessions = clientSessions;
        }

        private async Task<bool> ValidateSession(Guid userId) =>
            (await _clientSessions.FindUserIdAsync(
                Request.Headers["X-Session-Token"].FirstOrDefault(),
                userId,
                HttpContext.RequestAborted)).HasValue;

        [HttpGet("{id}")]
        public async Task<IActionResult> GetProfile(Guid id)
        {
            using var db = new NpgsqlConnection(_connString);
            if (!await ValidateSession(id)) return Unauthorized(new { erro = "Sessao expirada." });
            
            // Faz um JOIN na própria tabela para trazer o referral_code de quem o indicou
            var user = await db.QueryFirstOrDefaultAsync(
                @"SELECT u.id, u.name, u.email, u.whatsapp, u.referral_code, 
                         u.referred_by, r.referral_code as referred_by_code, 
                         u.created_at, u.used_referral_discount 
                  FROM users u 
                  LEFT JOIN users r ON u.referred_by = r.id 
                  WHERE u.id = @Id", 
                new { Id = id });
            
            if (user == null) return NotFound();

            DateTime createdAt = (DateTime)user.created_at;
            DateTime expiresAt = createdAt.AddHours(12);
            
            // Regra de Negócio: Foi indicado? Já usou o desconto? Está dentro das 12h?
            bool eligibleForDiscount = user.referred_by != null 
                                       && !user.used_referral_discount 
                                       && expiresAt > DateTime.Now;

            // HISTÓRICO DO CLIENTE: mantém os pedidos pagos e também exibe os cancelados.
            // Pedidos pendentes continuam no bloco próprio do PIX para não aparecerem duplicados.
            var ordersRaw = await db.QueryAsync(
                @"SELECT created_at AS ""CreatedAt"", period AS ""Period"", days AS ""Days"",
                         (created_at::date + days)::timestamp AS ""ExpiresAt"",
                         computers AS ""Computers"", wyds_per_computer AS ""WydsPerComputer"",
                         total_price AS ""TotalPrice"", status AS ""Status"",
                         delivered AS ""Delivered"", delivered_at AS ""DeliveredAt"",
                         canceled_at AS ""CanceledAt"", COALESCE(refunded, false) AS ""Refunded"",
                         COALESCE(canceled_was_paid, false) AS ""CanceledWasPaid""
                  FROM orders
                  WHERE user_id = @Id AND status IN ('pago', 'cancelado')
                  ORDER BY created_at DESC",
                new { Id = id });

            var orders = ordersRaw.Select(order => new
            {
                createdAt = (DateTime)order.CreatedAt,
                period = (string)order.Period,
                days = (int)order.Days,
                expiresAt = (DateTime)order.ExpiresAt,
                computers = (int)order.Computers,
                wydsPerComputer = (int)order.WydsPerComputer,
                totalPrice = (decimal)order.TotalPrice,
                status = (string)order.Status,
                delivered = (bool)order.Delivered,
                deliveredAt = order.DeliveredAt as DateTime?,
                canceledAt = order.CanceledAt as DateTime?,
                refunded = (bool)order.Refunded,
                canceledWasPaid = (bool)order.CanceledWasPaid
            });

            return Ok(new { 
                user = new {
                    user.id, user.name, user.email, user.whatsapp, 
                    user.referral_code, user.referred_by_code
                }, 
                eligibleForDiscount,
                discountExpiresAt = expiresAt,
                orders = orders
            });
        }



        [HttpPost("referral")]
        public async Task<IActionResult> SetReferralCode([FromBody] SetReferralRequest req)
        {
            using var db = new NpgsqlConnection(_connString);
            if (!await ValidateSession(req.UserId)) return Unauthorized(new { erro = "Sessao expirada." });
            string code = req.Code.Trim().ToUpper();
            
            var exists = await db.QueryFirstOrDefaultAsync<int>("SELECT 1 FROM users WHERE referral_code = @Code", new { Code = code });
            if (exists == 1) return BadRequest(new { erro = "Este código já está em uso." });

            await db.ExecuteAsync("UPDATE users SET referral_code = @Code WHERE id = @Id", new { Code = code, Id = req.UserId });
            return Ok(new { success = true });
        }
		
		[HttpGet("referral-count/{userId}")]
		public async Task<IActionResult> GetReferralCount(Guid userId)
		{
			using var db = new NpgsqlConnection(_connString);
			
			// Pega o código de indicação do usuário
			if (!await ValidateSession(userId)) return Unauthorized(new { erro = "Sessao expirada." });
			string? referralCode = await db.QueryFirstOrDefaultAsync<string>(
				"SELECT referral_code FROM users WHERE id = @Id",
				new { Id = userId });
			
			if (string.IsNullOrEmpty(referralCode))
				return Ok(new { count = 0, referralCode = (string?)null });
			
			// Conta quantos usuários foram indicados por este código
			int count = await db.QueryFirstOrDefaultAsync<int>(
				@"SELECT COUNT(*) FROM users WHERE referred_by = @UserId",
				new { UserId = userId });
			
			return Ok(new { count = count, referralCode = referralCode });
		}

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateProfileRequest req, [FromServices] PremierAPI.Services.ActiveDirectoryService ad)
        {
            using var db = new NpgsqlConnection(_connString);
            string? rawSessionToken =
                Request.Headers["X-Session-Token"].FirstOrDefault();
            bool changingPassword =
                !string.IsNullOrWhiteSpace(req.CurrentPassword) ||
                !string.IsNullOrWhiteSpace(req.NewPassword);
            ClientSessionIssue? rotatedSession = null;
            string? adUsername;

            if (changingPassword)
            {
                if (string.IsNullOrWhiteSpace(req.CurrentPassword))
                    return BadRequest(new { erro = "Informe a senha atual para alterar a senha." });
                if (string.IsNullOrWhiteSpace(req.NewPassword) ||
                    req.NewPassword.Length < 6 ||
                    req.NewPassword.Length > 72)
                {
                    return BadRequest(new { erro = "A nova senha deve ter no mínimo 6 caracteres." });
                }

                await db.OpenAsync();
                await using var transaction = await db.BeginTransactionAsync();
                Guid? authenticatedUserId = await _clientSessions.FindUserIdAsync(
                    db,
                    transaction,
                    rawSessionToken,
                    id,
                    lockUser: true,
                    HttpContext.RequestAborted);
                if (!authenticatedUserId.HasValue)
                {
                    await transaction.RollbackAsync();
                    return Unauthorized(new { erro = "Sessao expirada." });
                }

                var user = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT password_hash, ad_username FROM users WHERE id = @Id",
                    new { Id = id },
                    transaction);
                if (user == null)
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { erro = "Usuário não encontrado." });
                }
                if (!BCrypt.Net.BCrypt.Verify(
                        req.CurrentPassword,
                        (string)user.password_hash))
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { erro = "A senha atual está incorreta." });
                }

                string newHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, 12);
                await db.ExecuteAsync(
                    "UPDATE users SET whatsapp = @Whatsapp, password_hash = @Hash WHERE id = @Id",
                    new { Whatsapp = req.Whatsapp, Hash = newHash, Id = id },
                    transaction);
                adUsername = (string?)user.ad_username;
                if (string.IsNullOrWhiteSpace(adUsername))
                {
                    string protectedPassword = _adPasswordProtection.Protect(req.NewPassword!);
                    await db.ExecuteAsync(@"
                        INSERT INTO pending_ad_credentials (user_id, protected_password, updated_at)
                        VALUES (@UserId, @ProtectedPassword, CURRENT_TIMESTAMP)
                        ON CONFLICT (user_id) DO UPDATE
                        SET protected_password = EXCLUDED.protected_password,
                            updated_at = CURRENT_TIMESTAMP",
                        new { UserId = id, ProtectedPassword = protectedPassword }, transaction);
                }
                rotatedSession = await _clientSessions.RotateSessionAsync(
                    db,
                    transaction,
                    id,
                    HttpContext.RequestAborted);
                await transaction.CommitAsync();
            }
            else
            {
                if (!await ValidateSession(id))
                    return Unauthorized(new { erro = "Sessao expirada." });
                await db.ExecuteAsync("UPDATE users SET whatsapp = @Whatsapp WHERE id = @Id", 
                    new { Whatsapp = req.Whatsapp, Id = id });
                adUsername = await db.QueryFirstOrDefaultAsync<string>(
                    "SELECT ad_username FROM users WHERE id = @Id",
                    new { Id = id });
            }

            // Reconsulta o vínculo após a gravação para cobrir provisionamento concorrente.
            adUsername = await db.QueryFirstOrDefaultAsync<string>(
                "SELECT ad_username FROM users WHERE id = @Id", new { Id = id });
            if (!string.IsNullOrWhiteSpace(adUsername))
            {
                try {
                    if (changingPassword && !string.IsNullOrWhiteSpace(req.NewPassword))
                    {
                        await ad.SetUserPasswordAsync(adUsername, req.NewPassword, forceChangeOnNextLogon: false);
                        await db.ExecuteAsync("DELETE FROM pending_ad_credentials WHERE user_id = @Id", new { Id = id });
                    }
                    if (!string.IsNullOrWhiteSpace(req.Whatsapp)) {
                        await ad.UpdateTelephoneAsync(adUsername, req.Whatsapp);
                    }
                } catch (Exception ex) {
                    _logger.LogError(ex, "[AD SYNC] Falha ao sincronizar perfil do usuario {UserId} para o AD.", id);
                }
            }

            return Ok(new
            {
                success = true,
                msg = "Perfil atualizado com sucesso.",
                token = rotatedSession?.Token,
                expiresAt = rotatedSession?.ExpiresAt
            });
        }
    }
    public class SetReferralRequest { public Guid UserId { get; set; } public string Code { get; set; } = ""; }
    public class UpdateProfileRequest { public string? Whatsapp { get; set; } public string? CurrentPassword { get; set; } public string? NewPassword { get; set; } }
}
