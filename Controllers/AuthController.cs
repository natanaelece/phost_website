// AuthController.cs
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MailKit.Net.Smtp;
using MimeKit;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using BCrypt.Net;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using PremierAPI.Services;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/auth")]
    [EnableRateLimiting("AuthLimiter")]
    public class AuthController : ControllerBase
    {
        private readonly string _connString;
        private readonly ILogger<AuthController> _logger;
        private readonly bool _debugLogs;
        private readonly string _turnstileSecret;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly EmailConfirmationService _emailConfirmation;
        private readonly AdPasswordProtectionService _adPasswordProtection;

        public AuthController(
            IConfiguration config,
            ILogger<AuthController> logger,
            IHttpClientFactory httpClientFactory,
            EmailConfirmationService emailConfirmation,
            AdPasswordProtectionService adPasswordProtection)
        {
            _config = config;
            _connString = config.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
            _debugLogs = config.GetValue<bool>("PremierConfig:EnableDebugLogs");
            _turnstileSecret = config.GetValue<string>("Cloudflare:TurnstileSecretKey") ?? "";
            _httpClientFactory = httpClientFactory;
            _emailConfirmation = emailConfirmation;
            _adPasswordProtection = adPasswordProtection;
        }

        // =========================================================================
        // LOGIN
        // =========================================================================
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { erro = "E-mail e senha são obrigatórios." });

            if (string.IsNullOrWhiteSpace(req.TurnstileResponse))
                return BadRequest(new { erro = "Validação anti-bot obrigatória (Captcha ausente)." });

            bool isCaptchaValid = await ValidarTurnstile(req.TurnstileResponse);
            if (!isCaptchaValid)
            {
                _logger.LogInformation("[LOGIN BLOQUEADO] Falha no Captcha Turnstile para: {Email}", req.Email);
                return BadRequest(new { erro = "Falha na validação do Captcha anti-bot." });
            }

            if (_debugLogs) _logger.LogInformation("[LOGIN] Tentativa de acesso: {Email}", req.Email);

            using var db = new NpgsqlConnection(_connString);

            // Busca usuário pelo e-mail apenas (não pelo hash — necessário para BCrypt.Verify)
            var user = await db.QueryFirstOrDefaultAsync<UserAuthDto>(
                "SELECT id, name, email, whatsapp, email_confirmed, password_hash, is_active, ad_username FROM users WHERE email = @Email",
                new { Email = req.Email });

            if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.Password_Hash))
            {
                _logger.LogInformation("[LOGIN NEGADO] E-mail ou senha incorretos: {Email}", req.Email);
                return Unauthorized(new { erro = "E-mail ou senha incorretos." });
            }

            if (!user.Email_Confirmed)
            {
                _logger.LogInformation("[LOGIN NEGADO] E-mail não confirmado: {Email}", req.Email);
                return Unauthorized(new { erro = "E-mail não confirmado. Verifique sua caixa de entrada." });
            }

            // Gera e persiste o token de sessão (autenticação real no banco)
            if (!user.Is_Active)
            {
                _logger.LogInformation("[LOGIN NEGADO] Conta inativa: {Email}", req.Email);
                return Unauthorized(new { erro = "Conta inativa. Entre em contato com o suporte." });
            }

            if (string.IsNullOrWhiteSpace(user.Ad_Username))
            {
                string protectedPassword = _adPasswordProtection.Protect(req.Password);
                await db.ExecuteAsync(@"
                    INSERT INTO pending_ad_credentials (user_id, protected_password, updated_at)
                    VALUES (@UserId, @ProtectedPassword, CURRENT_TIMESTAMP)
                    ON CONFLICT (user_id) DO UPDATE
                    SET protected_password = EXCLUDED.protected_password,
                        updated_at = CURRENT_TIMESTAMP",
                    new { UserId = user.Id, ProtectedPassword = protectedPassword });
            }

            string sessionToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            DateTime sessionExpires = DateTime.UtcNow.AddDays(7);

            // Multiple sessions support: insere nova sessao sem derrubar as existentes
            await db.ExecuteAsync(
                "INSERT INTO user_sessions (user_id, token, expires_at) VALUES (@Id, @Token, @Expires)",
                new { Id = user.Id, Token = sessionToken, Expires = sessionExpires });

            if (_debugLogs) _logger.LogInformation("[LOGIN SUCESSO] {Email}", req.Email);

            bool isAdmin = user.Email == _config["AdminEmail"];
            return Ok(new
            {
                token = sessionToken,
                isAdmin = isAdmin,
                adminToken = isAdmin ? _config["AdminToken"] : null,
                user = new UserDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Whatsapp = user.Whatsapp
                }
            });
        }

        // =========================================================================
        // REGISTER
        // =========================================================================
        [HttpPost("validate-referral")]
        public async Task<IActionResult> ValidateReferral([FromBody] ValidateReferralRequest req)
        {
            string code = (req.Code ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code)) return Ok(new { valid = true });
            if (code.Length > 20) return Ok(new { valid = false });

            using var db = new NpgsqlConnection(_connString);
            bool exists = await db.QueryFirstOrDefaultAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users WHERE referral_code = @Code)",
                new { Code = code });

            return Ok(new { valid = exists });
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email) ||
                string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { erro = "Nome, e-mail e senha são obrigatórios." });

            if (req.Password.Length < 6 || req.Password.Length > 72)
                return BadRequest(new { erro = "A senha deve ter entre 6 e 72 caracteres." });

            if (!System.Text.RegularExpressions.Regex.IsMatch(req.Name, @"^[a-zA-ZÀ-ÿ\s]+$"))
                return BadRequest(new { erro = "Nome inválido. Não utilize números ou caracteres especiais." });

            if (!System.Text.RegularExpressions.Regex.IsMatch(req.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return BadRequest(new { erro = "Formato de e-mail inválido. Utilize um padrão como usuario@dominio.com." });

            try
            {
                var addr = new System.Net.Mail.MailAddress(req.Email);
                if (addr.Address != req.Email)
                    throw new Exception("E-mail não corresponde.");
            }
            catch
            {
                return BadRequest(new { erro = "Formato de e-mail inválido." });
            }

            if (_debugLogs) _logger.LogInformation("[REGISTER] Novo cadastro: {Email} | Ref: {Ref}", req.Email, req.ReferralCode);

            if (string.IsNullOrWhiteSpace(req.TurnstileResponse))
                return BadRequest(new { erro = "Validação anti-bot obrigatória (Captcha ausente)." });

            bool isCaptchaValid = await ValidarTurnstile(req.TurnstileResponse);
            if (!isCaptchaValid)
            {
                _logger.LogInformation("[REGISTER BLOQUEADO] Falha no Captcha Turnstile para: {Email}", req.Email);
                return BadRequest(new { erro = "Falha na validação do Captcha anti-bot." });
            }

            using var db = new NpgsqlConnection(_connString);
            var exists = await db.QueryFirstOrDefaultAsync<int>("SELECT 1 FROM users WHERE email = @Email", new { req.Email });
            if (exists == 1) return BadRequest(new { erro = "E-mail já cadastrado." });

            Guid? referredBy = null;
            if (!string.IsNullOrWhiteSpace(req.ReferralCode))
            {
                referredBy = await db.QueryFirstOrDefaultAsync<Guid?>(
                    "SELECT id FROM users WHERE referral_code = @Code",
                    new { Code = req.ReferralCode.Trim().ToUpper() });
                if (referredBy == null)
                {
                    _logger.LogInformation("[REGISTER REJEITADO] Código de indicação inválido: {Code}", req.ReferralCode);
                    return BadRequest(new { erro = "Código de indicação inválido." });
                }
            }

            // BCrypt com work factor 12 (≈250ms por hash — resistente a brute force)
            string hash = BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 12);
            string protectedPassword = _adPasswordProtection.Protect(req.Password);
            string emailToken = Guid.NewGuid().ToString();

            DateTime registeredAt = GetConfiguredLocalNow();
            DateTime firstReminderAt = EmailConfirmationReminderWorker.GetFirstReminderAt(registeredAt);
            string sql = @"INSERT INTO users
                              (name, email, whatsapp, password_hash, referred_by,
                               email_confirmation_token, email_confirmed,
                               email_confirmation_resend_count, email_confirmation_last_sent_at,
                               email_confirmation_next_send_at, created_at)
                           VALUES
                              (@Name, @Email, @Whatsapp, @Hash, @RefId,
                               @EmailToken, false, 0, @RegisteredAt, @FirstReminderAt, @RegisteredAt)
                           RETURNING id";

            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            Guid userId = await db.QuerySingleAsync<Guid>(sql, new
            {
                req.Name,
                req.Email,
                req.Whatsapp,
                Hash = hash,
                RefId = referredBy,
                EmailToken = emailToken,
                RegisteredAt = registeredAt,
                FirstReminderAt = firstReminderAt
            }, transaction);
            await db.ExecuteAsync(@"
                INSERT INTO pending_ad_credentials (user_id, protected_password)
                VALUES (@UserId, @ProtectedPassword)",
                new { UserId = userId, ProtectedPassword = protectedPassword }, transaction);
            await transaction.CommitAsync();
            try
            {
                await _emailConfirmation.SendAsync(req.Email, req.Name, emailToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SMTP ERROR] Falha ao disparar o e-mail inicial de ativação.");
            }

            if (_debugLogs) _logger.LogInformation("[REGISTER SUCESSO] {Email} cadastrado.", req.Email);
            return Ok(new { success = true, mensagem = "Cadastro realizado! Verifique seu e-mail para ativar a conta." });
        }

        // =========================================================================
        // CONFIRM EMAIL
        // =========================================================================
        [HttpGet("confirm-email")]
        public async Task<IActionResult> ConfirmEmail([FromQuery] string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { erro = "Token de confirmação inválido." });

            using var db = new NpgsqlConnection(_connString);

            string sql = @"UPDATE users
                           SET email_confirmed = true,
                               email_confirmation_token = NULL,
                               email_confirmation_next_send_at = NULL
                           WHERE email_confirmation_token = @Token
                           RETURNING email";

            string? emailCadastrado = await db.QueryFirstOrDefaultAsync<string>(sql, new { Token = token });

            if (string.IsNullOrEmpty(emailCadastrado))
                return BadRequest(new { erro = "Token inválido ou já expirou." });

            return Ok(new { success = true, mensagem = "E-mail verificado com sucesso!", email = emailCadastrado });
        }

        // =========================================================================
        // FORGOT PASSWORD
        // =========================================================================
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email))
                return BadRequest(new { erro = "Informe seu e-mail." });

            if (string.IsNullOrWhiteSpace(req.TurnstileResponse))
                return BadRequest(new { erro = "Validação anti-bot obrigatória (Captcha ausente)." });

            bool isCaptchaValid = await ValidarTurnstile(req.TurnstileResponse);
            if (!isCaptchaValid)
            {
                _logger.LogWarning("[AUTH] Bloqueio Turnstile no ForgotPassword. Email: {Email}", req.Email);
                return BadRequest(new { erro = "Falha na validação anti-bot. Tente novamente." });
            }

            using var db = new NpgsqlConnection(_connString);
            var user = await db.QueryFirstOrDefaultAsync(
                "SELECT id, name, email FROM users WHERE email = @Email AND email_confirmed = true AND is_active = true",
                new { Email = req.Email });

            if (user == null)
            {
                // Não revela se o e-mail existe (segurança)
                _logger.LogInformation("[RECOVER] E-mail não encontrado ou não confirmado: {Email}", req.Email);
                return Ok(new { success = true, mensagem = "Se o e-mail estiver cadastrado, você receberá um link de recuperação." });
            }

            string resetToken = Guid.NewGuid().ToString("N");
            DateTime expiresAt = DateTime.UtcNow.AddHours(1);

            await db.ExecuteAsync(
                "UPDATE users SET password_reset_token = @Token, password_reset_expires = @Expires WHERE email = @Email",
                new { Token = resetToken, Expires = expiresAt, Email = req.Email });

            await EnviarEmailRecuperacao(user.email, user.name, resetToken);

            if (_debugLogs) _logger.LogInformation("[RECOVER] Token gerado para {Email}", req.Email);
            return Ok(new { success = true, mensagem = "Se o e-mail estiver cadastrado, você receberá um link de recuperação." });
        }

        // =========================================================================
        // RESET PASSWORD
        // =========================================================================
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.NewPassword))
                return BadRequest(new { erro = "Token e nova senha são obrigatórios." });

            if (req.NewPassword.Length < 6 || req.NewPassword.Length > 72)
                return BadRequest(new { erro = "A senha deve ter entre 6 e 72 caracteres." });

            using var db = new NpgsqlConnection(_connString);

            var user = await db.QueryFirstOrDefaultAsync(
                "SELECT id, email, ad_username FROM users WHERE password_reset_token = @Token AND password_reset_expires > @Now",
                new { Token = req.Token, Now = DateTime.UtcNow });

            if (user == null)
                return BadRequest(new { erro = "Token inválido ou expirado. Solicite um novo link de recuperação." });

            // BCrypt com work factor 12
            string hash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, workFactor: 12);

            await db.OpenAsync();
            await using (var transaction = await db.BeginTransactionAsync())
            {
                await db.ExecuteAsync(
                    "UPDATE users SET password_hash = @Hash, password_reset_token = NULL, password_reset_expires = NULL WHERE id = @Id",
                    new { Hash = hash, Id = user.id }, transaction);

                if (string.IsNullOrWhiteSpace((string?)user.ad_username))
                {
                    string protectedPassword = _adPasswordProtection.Protect(req.NewPassword);
                    await db.ExecuteAsync(@"
                        INSERT INTO pending_ad_credentials (user_id, protected_password, updated_at)
                        VALUES (@UserId, @ProtectedPassword, CURRENT_TIMESTAMP)
                        ON CONFLICT (user_id) DO UPDATE
                        SET protected_password = EXCLUDED.protected_password,
                            updated_at = CURRENT_TIMESTAMP",
                        new { UserId = (Guid)user.id, ProtectedPassword = protectedPassword }, transaction);
                }

                await transaction.CommitAsync();
            }
                
            // [NEW] Sync whatsapp/password to AD
            var userInfo = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT ad_username, whatsapp FROM users WHERE id = @Id", new { Id = user.id });
            if (userInfo != null)
            {
                string? adUser = (string?)userInfo.ad_username;
                if (!string.IsNullOrWhiteSpace(adUser))
                {
                    var ad = HttpContext.RequestServices.GetService(typeof(PremierAPI.Services.ActiveDirectoryService)) as PremierAPI.Services.ActiveDirectoryService;
                    if (ad != null)
                    {
                        try {
                            string? adUsername = (string?)userInfo.ad_username;
                            string? whatsapp = (string?)userInfo.whatsapp;
                            if (!string.IsNullOrWhiteSpace(adUsername))
                            {
                                await ad.SetUserPasswordAsync(adUsername, req.NewPassword, forceChangeOnNextLogon: false);
                                await db.ExecuteAsync("DELETE FROM pending_ad_credentials WHERE user_id = @UserId", new { UserId = (Guid)user.id });
                            }
                            if (!string.IsNullOrWhiteSpace(whatsapp) && !string.IsNullOrWhiteSpace(adUsername))
                                await ad.UpdateTelephoneAsync(adUsername, whatsapp);
                        } catch (Exception ex) {
                            _logger.LogError(ex, "[AD SYNC] Falha ao sincronizar nova senha e whatsapp para o AD no reset.");
                        }
                    }
                }
            }

            _logger.LogInformation("[RESET PASSWORD] Senha alterada para: {Email}", (string)user.email);

            return Ok(new { success = true, mensagem = "Senha alterada com sucesso! Faça login com sua nova senha.", email = (string)user.email });
        }

        // =========================================================================
        // VALIDATE RESET TOKEN
        // =========================================================================
        [HttpGet("validate-reset-token")]
        public async Task<IActionResult> ValidateResetToken([FromQuery] string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { valid = false, erro = "Token não informado." });

            using var db = new NpgsqlConnection(_connString);

            var exists = await db.QueryFirstOrDefaultAsync<int>(
                @"SELECT COUNT(*)
                  FROM users
                  WHERE password_reset_token = @Token
                  AND password_reset_expires > @Now",
                new { Token = token, Now = DateTime.UtcNow });

            if (exists <= 0)
                return BadRequest(new { valid = false, erro = "Token inválido ou expirado." });

            return Ok(new { valid = true });
        }

        // =========================================================================
        // PRIVATE HELPERS
        // =========================================================================
        private async Task<bool> ValidarTurnstile(string captchaToken)
        {
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

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[TURNSTILE REJETO] Servidor Cloudflare retornou status: {Status}", response.StatusCode);
                    return false;
                }

                var result = await response.Content.ReadFromJsonAsync<TurnstileResponse>();
                return result?.Success ?? false;
            }
            catch (Exception ex)
            {
                _logger.LogError("[TURNSTILE ERROR] Falha crítica na API Cloudflare: {Msg}", ex.Message);
                return false;
            }
        }

        private async Task EnviarEmailRecuperacao(string email, string name, string token)
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
                message.Subject = "Recuperação de senha — Premier Host";

                string baseUrl = _config["PremierConfig:BaseUrlFront"]!;
                string linkReset = $"{baseUrl}/recuperar-senha?token={token}";

                message.Body = new TextPart("html")
                {
                    Text = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto;'>
                        <h2 style='color: #3b82f6; font-size: 22px; margin-bottom: 20px;'>Recuperação de senha — Premier Host</h2>
                        <p style='font-size: 14px;'>Olá, <strong>{name}</strong>!</p>
                        <p style='font-size: 14px; line-height: 1.5; margin-bottom: 20px;'>Recebemos uma solicitação de redefinição de senha para sua conta. Clique no botão abaixo para criar uma nova senha:</p>
                        <a href='{linkReset}' style='display: inline-block; padding: 12px 28px; background-color: #2563eb; color: #ffffff !important; border-radius: 8px; font-weight: bold; text-decoration: none; margin: 14px 0;'>Redefinir senha</a>
                        <p style='color: #999999; font-size: 13px; margin-top: 20px;'>Este link expira em 1 hora. Se não solicitou a recuperação, ignore este e-mail.</p>
                        <hr style='border: 0; border-top: 1px solid #eeeeee; margin: 24px 0;'>
                        <p style='color: #999999; font-size: 12px; margin: 0;'>Premier Host — Não responda este e-mail.</p>
                    </div>"
                };

                using var client = new SmtpClient();
                await client.ConnectAsync(smtpServer, smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(smtpUser, smtpPassword);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("[EMAIL RECOVER] E-mail de recuperação enviado para {Email}", email);
            }
            catch (Exception ex)
            {
                _logger.LogError("[SMTP ERROR] Falha ao enviar e-mail de recuperação para {Email}: {Msg}", email, ex.Message);
            }
        }

        private DateTime GetConfiguredLocalNow()
        {
            string timeZoneId = _config["EmailConfirmationReminders:TimeZone"] ?? "";
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            }
            catch
            {
                return DateTime.Now;
            }
        }
    }

    // =========================================================================
    // DTOs
    // =========================================================================
    public class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        [JsonPropertyName("cf-turnstile-response")] public string TurnstileResponse { get; set; } = "";
    }

    public class RegisterRequest
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Whatsapp { get; set; } = "";
        public string Password { get; set; } = "";
        public string ReferralCode { get; set; } = "";
        [JsonPropertyName("cf-turnstile-response")] public string TurnstileResponse { get; set; } = "";
    }

    public class ValidateReferralRequest
    {
        public string Code { get; set; } = "";
    }

    public class ForgotPasswordRequest
    {
        public string Email { get; set; } = "";
        [JsonPropertyName("cf-turnstile-response")] public string TurnstileResponse { get; set; } = "";
    }

    public class ResetPasswordRequest
    {
        public string Token { get; set; } = "";
        public string NewPassword { get; set; } = "";
    }

    // DTO interno para busca com hash (nunca serializado para o cliente)
    internal class UserAuthDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Whatsapp { get; set; } = "";
        public bool Email_Confirmed { get; set; }
        public bool Is_Active { get; set; } = true;
        public string? Ad_Username { get; set; }
        [JsonIgnore] public string Password_Hash { get; set; } = "";
    }

    // DTO público retornado ao cliente
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Whatsapp { get; set; } = "";
    }

    public class TurnstileResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
    }
}


