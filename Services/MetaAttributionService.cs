using Dapper;
using Npgsql;

namespace PremierAPI.Services;

public sealed record MetaAttributionCapture(
    Guid AttributionId,
    string ConsentStatus,
    string ConsentVersion,
    string? Fbp,
    string? Fbc,
    string? Fbclid,
    string? SourceUrl,
    string? ClientIpAddress,
    string? ClientUserAgent,
    string? SessionToken);

public sealed class MetaAttributionService
{
    public const string ConsentCookieName = "phost_marketing_consent";
    public const string ConsentVersionCookieName = "phost_marketing_consent_version";
    public const string AttributionCookieName = "phost_meta_attribution";

    private readonly string _connectionString;
    private readonly MetaConversionsOptions _options;
    private readonly ILogger<MetaAttributionService> _logger;

    public MetaAttributionService(
        IConfiguration configuration,
        MetaConversionsOptions options,
        ILogger<MetaAttributionService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        _options = options;
        _logger = logger;
    }

    public string ConsentVersion => _options.ConsentVersion;

    public async Task CaptureAsync(
        MetaAttributionCapture capture,
        CancellationToken cancellationToken = default)
    {
        bool accepted = string.Equals(capture.ConsentStatus, "accepted", StringComparison.Ordinal);
        string consentStatus = accepted ? "accepted" : "rejected";
        Guid? verifiedUserId = null;

        await using var db = new NpgsqlConnection(_connectionString);
        if (!string.IsNullOrWhiteSpace(capture.SessionToken))
        {
            verifiedUserId = await db.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(@"
                SELECT s.user_id
                FROM user_sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token = @Token
                  AND s.expires_at > @Now
                  AND u.is_active = true
                LIMIT 1;",
                new { Token = capture.SessionToken, Now = DateTime.UtcNow },
                cancellationToken: cancellationToken));
        }

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO meta_attributions
                (id, user_id, consent_status, consent_version, consented_at, revoked_at,
                 fbp, fbc, fbclid, client_ip_address, client_user_agent, source_url,
                 captured_at, updated_at)
            VALUES
                (@Id, @UserId, @ConsentStatus, @ConsentVersion,
                 CASE WHEN @Accepted THEN CURRENT_TIMESTAMP ELSE NULL END,
                 CASE WHEN @Accepted THEN NULL ELSE CURRENT_TIMESTAMP END,
                 @Fbp, @Fbc, @Fbclid, CAST(@ClientIpAddress AS inet), @ClientUserAgent,
                 @SourceUrl, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (id) DO UPDATE
            SET user_id = COALESCE(meta_attributions.user_id, EXCLUDED.user_id),
                consent_status = EXCLUDED.consent_status,
                consent_version = EXCLUDED.consent_version,
                consented_at = CASE
                    WHEN EXCLUDED.consent_status = 'accepted'
                    THEN COALESCE(meta_attributions.consented_at, CURRENT_TIMESTAMP)
                    ELSE meta_attributions.consented_at
                END,
                revoked_at = CASE
                    WHEN EXCLUDED.consent_status = 'rejected' THEN CURRENT_TIMESTAMP
                    ELSE NULL
                END,
                fbp = EXCLUDED.fbp,
                fbc = EXCLUDED.fbc,
                fbclid = EXCLUDED.fbclid,
                client_ip_address = EXCLUDED.client_ip_address,
                client_user_agent = EXCLUDED.client_user_agent,
                source_url = EXCLUDED.source_url,
                updated_at = CURRENT_TIMESTAMP;",
            new
            {
                Id = capture.AttributionId,
                UserId = verifiedUserId,
                ConsentStatus = consentStatus,
                ConsentVersion = Truncate(capture.ConsentVersion, 20) ?? _options.ConsentVersion,
                Accepted = accepted,
                Fbp = accepted ? Truncate(capture.Fbp, 255) : null,
                Fbc = accepted ? Truncate(capture.Fbc, 500) : null,
                Fbclid = accepted ? Truncate(capture.Fbclid, 500) : null,
                ClientIpAddress = accepted ? Truncate(capture.ClientIpAddress, 64) : null,
                ClientUserAgent = accepted ? Truncate(capture.ClientUserAgent, 512) : null,
                SourceUrl = accepted
                    ? MetaConversionsService.CanonicalizeSourceUrl(capture.SourceUrl)
                    : null
            },
            cancellationToken: cancellationToken));

        if (!accepted)
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE meta_attributions
                SET consent_status = 'rejected',
                    consent_version = @ConsentVersion,
                    revoked_at = CURRENT_TIMESTAMP,
                    fbp = NULL,
                    fbc = NULL,
                    fbclid = NULL,
                    client_ip_address = NULL,
                    client_user_agent = NULL,
                    source_url = NULL,
                    updated_at = CURRENT_TIMESTAMP
                WHERE source_attribution_id = @AttributionId;",
                new
                {
                    AttributionId = capture.AttributionId,
                    ConsentVersion = _options.ConsentVersion
                },
                cancellationToken: cancellationToken));

            if (verifiedUserId.HasValue)
            {
                await db.ExecuteAsync(new CommandDefinition(@"
                    UPDATE meta_attributions
                    SET consent_status = 'rejected',
                        consent_version = @ConsentVersion,
                        revoked_at = CURRENT_TIMESTAMP,
                        fbp = NULL,
                        fbc = NULL,
                        fbclid = NULL,
                        client_ip_address = NULL,
                        client_user_agent = NULL,
                        source_url = NULL,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE user_id = @UserId;",
                    new
                    {
                        UserId = verifiedUserId.Value,
                        ConsentVersion = _options.ConsentVersion
                    },
                    cancellationToken: cancellationToken));
            }
        }
    }

    public async Task TryAssociateWithUserAsync(
        Guid? attributionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!attributionId.HasValue) return;

        try
        {
            await using var db = new NpgsqlConnection(_connectionString);
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE meta_attributions
                SET user_id = @UserId,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @AttributionId
                  AND consent_status = 'accepted';",
                new { UserId = userId, AttributionId = attributionId.Value },
                cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META ATRIBUICAO] Falha ao associar atribuição ao usuário {UserId}.",
                userId);
        }
    }

    public static Guid? ParseAttributionId(string? value) =>
        Guid.TryParse(value, out Guid attributionId) ? attributionId : null;

    private static string? Truncate(string? value, int maxLength)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.Length == 0) return null;
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }
}
