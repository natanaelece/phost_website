using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PremierAPI.Services
{
    public class AdAccountProvisioningService
    {
        private readonly string _connectionString;
        private readonly ActiveDirectoryService _ad;
        private readonly AdCredentialEmailService _credentialEmail;
        private readonly ILogger<AdAccountProvisioningService> _logger;
        private readonly SemaphoreSlim _provisioningLock = new(1, 1);

        public AdAccountProvisioningService(
            IConfiguration config,
            ActiveDirectoryService ad,
            AdCredentialEmailService credentialEmail,
            ILogger<AdAccountProvisioningService> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection") ?? "";
            _ad = ad;
            _credentialEmail = credentialEmail;
            _logger = logger;
        }

        public async Task<bool> TryProvisionOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            try
            {
                await ProvisionOrderAsync(orderId, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(orderId, ex.Message);
                _logger.LogError(ex, "[AD PROVISIONAMENTO] Falha ao provisionar o pedido {OrderId}.", orderId);
                return false;
            }
        }

        private async Task ProvisionOrderAsync(Guid orderId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_connectionString)) throw new InvalidOperationException("Banco de dados não configurado.");
            await _provisioningLock.WaitAsync(cancellationToken);
            try
            {
                using var db = new NpgsqlConnection(_connectionString);
                var order = await db.QueryFirstOrDefaultAsync<ProvisioningOrder>(@"
                    SELECT o.id AS Id, o.user_id AS UserId, o.status AS Status,
                           o.ad_provisioned_at AS AdProvisionedAt,
                           u.name AS UserName, u.email AS Email, u.whatsapp AS Whatsapp,
                           u.ad_username AS AdUsername,
                           u.ad_credentials_delivered_at AS AdCredentialsDeliveredAt
                    FROM orders o
                    JOIN users u ON u.id = o.user_id
                    WHERE o.id = @OrderId", new { OrderId = orderId });

                if (order == null) throw new InvalidOperationException("Pedido não encontrado para provisionamento.");
                if (!string.Equals(order.Status, "pago", StringComparison.OrdinalIgnoreCase)) return;
                if (order.AdProvisionedAt.HasValue) return;

                DateTime expiresOn = await db.QuerySingleAsync<DateTime>(@"
                    SELECT MAX(o.created_at::date + o.days)::timestamp AS ExpirationDate
                    FROM orders o
                    WHERE o.user_id = @UserId AND o.status = 'pago'",
                    new { order.UserId });

                string? temporaryPassword = null;
                string username = order.AdUsername?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(username))
                {
                    username = await GenerateAvailableUsernameAsync(db, order.UserName, order.Email);
                    temporaryPassword = GenerateTemporaryPassword();
                    await _ad.CreateUserAsync(
                        username,
                        order.UserName,
                        temporaryPassword,
                        order.Email,
                        order.Whatsapp,
                        fromWebsite: false,
                        forcePasswordChange: false,
                        commonName: BuildCommonName(order.UserName, username));

                    int linked = await db.ExecuteAsync(@"
                        UPDATE users
                        SET ad_username = @Username, ad_credentials_delivered_at = NULL
                        WHERE id = @UserId AND ad_username IS NULL",
                        new { Username = username, order.UserId });
                    if (linked == 0)
                    {
                        await _ad.DeleteUserAsync(username);
                        throw new InvalidOperationException("O cadastro foi vinculado por outro processo durante o provisionamento.");
                    }
                }
                else
                {
                    if (!await _ad.UserExistsAsync(username))
                        throw new InvalidOperationException($"O usuário AD vinculado '{username}' não foi encontrado.");
                    await _ad.ActivateAndRestoreUserAsync(username);
                }

                await _ad.SetUserExpirationAsync(username, expiresOn);

                DateTime? credentialsDeliveredAt = order.AdCredentialsDeliveredAt;
                if (!credentialsDeliveredAt.HasValue)
                {
                    temporaryPassword ??= GenerateTemporaryPassword();
                    if (order.AdUsername != null)
                        await _ad.SetUserPasswordAsync(username, temporaryPassword, forceChangeOnNextLogon: false);

                    await _credentialEmail.SendAsync(order.Email, order.UserName, username, temporaryPassword, cancellationToken);
                    DateTime sentAt = DateTime.Now;
                    await db.ExecuteAsync(
                        "UPDATE users SET ad_credentials_delivered_at = @SentAt WHERE id = @UserId",
                        new { SentAt = sentAt, order.UserId });
                }

                await db.ExecuteAsync(@"
                    UPDATE orders
                    SET ad_provisioned_at = CURRENT_TIMESTAMP,
                        ad_provisioning_error = NULL,
                        ad_provisioning_next_attempt_at = NULL
                    WHERE id = @OrderId",
                    new { OrderId = order.Id });
                _logger.LogInformation("[AD PROVISIONAMENTO] Pedido {OrderId} vinculado ao usuario AD {AdUsername} ate {ExpiresOn:yyyy-MM-dd}.", order.Id, username, expiresOn);
            }
            finally
            {
                _provisioningLock.Release();
            }
        }

        private async Task<string> GenerateAvailableUsernameAsync(NpgsqlConnection db, string name, string email)
        {
            string baseName = NormalizeUsername(name);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = NormalizeUsername(email.Split('@')[0]);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "usuario";

            for (int sequence = 1; sequence <= 999; sequence++)
            {
                string suffix = sequence == 1 ? "" : sequence.ToString(CultureInfo.InvariantCulture);
                string candidate = baseName[..Math.Min(baseName.Length, 20 - suffix.Length)] + suffix;
                int localLinks = await db.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM users WHERE LOWER(ad_username) = LOWER(@Username)",
                    new { Username = candidate });
                if (localLinks == 0 && !await _ad.UserExistsAsync(candidate)) return candidate;
            }

            throw new InvalidOperationException("Não foi possível gerar um nome de usuário AD disponível.");
        }

        private static string NormalizeUsername(string value)
        {
            string decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (char character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character) || character is '.' or '_' or '-') builder.Append(character);
            }
            return builder.ToString().Normalize(NormalizationForm.FormC)[..Math.Min(builder.Length, 20)];
        }

        private static string GenerateTemporaryPassword()
        {
            return "Ph!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(7)) + "a9";
        }

        private static string BuildCommonName(string fullName, string username)
        {
            string suffix = $" ({username})";
            string normalizedName = (fullName ?? "").Trim();
            int availableLength = Math.Max(1, 64 - suffix.Length);
            if (normalizedName.Length > availableLength) normalizedName = normalizedName[..availableLength].TrimEnd();
            return normalizedName + suffix;
        }

        private async Task RecordFailureAsync(Guid orderId, string error)
        {
            if (string.IsNullOrWhiteSpace(_connectionString)) return;
            try
            {
                using var db = new NpgsqlConnection(_connectionString);
                int attempts = await db.QuerySingleOrDefaultAsync<int>(
                    "SELECT COALESCE(ad_provisioning_attempts, 0) FROM orders WHERE id = @OrderId",
                    new { OrderId = orderId });
                int nextAttempts = attempts + 1;
                int retryMinutes = Math.Min(60, Math.Max(5, nextAttempts * 5));
                await db.ExecuteAsync(@"
                    UPDATE orders
                    SET ad_provisioning_attempts = @Attempts,
                        ad_provisioning_error = @Error,
                        ad_provisioning_next_attempt_at = @NextAttempt
                    WHERE id = @OrderId",
                    new
                    {
                        OrderId = orderId,
                        Attempts = nextAttempts,
                        Error = error.Length > 500 ? error[..500] : error,
                        NextAttempt = DateTime.Now.AddMinutes(retryMinutes)
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AD PROVISIONAMENTO] Falha ao registrar erro do pedido {OrderId}.", orderId);
            }
        }

        private sealed class ProvisioningOrder
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public string Status { get; set; } = "";
            public DateTime? AdProvisionedAt { get; set; }
            public string UserName { get; set; } = "";
            public string Email { get; set; } = "";
            public string? Whatsapp { get; set; }
            public string? AdUsername { get; set; }
            public DateTime? AdCredentialsDeliveredAt { get; set; }
        }
    }
}
