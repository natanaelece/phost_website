using Dapper;
using Npgsql;

namespace PremierAPI.Services;

public enum EmailConfirmationPreparationStatus
{
    Prepared,
    NotFound,
    AlreadyConfirmed,
    SameDayConfirmationRequired,
    DeliveryInProgress
}

public enum EmailConfirmationResultStatus
{
    Confirmed,
    InvalidOrUsed,
    NotFound,
    AlreadyConfirmed
}

public sealed record EmailConfirmationTokenIssue(
    long TokenId,
    Guid UserId,
    string Token,
    string Name,
    string Email,
    string DeliveryKind);

public sealed record EmailConfirmationPreparationResult(
    EmailConfirmationPreparationStatus Status,
    EmailConfirmationTokenIssue? Issue = null,
    DateTime? LastSentAt = null);

public sealed record EmailConfirmationResult(
    EmailConfirmationResultStatus Status,
    Guid? UserId = null,
    string? Name = null,
    string? Email = null);

public sealed record EmailConfirmationDeliveryResult(
    bool Marked,
    Guid? UserId = null,
    int? ReminderCount = null,
    DateTime? NextSendAt = null);

public sealed record EmailConfirmationCleanupResult(
    int RecoveredClaims,
    int DeletedOldTokens);

public sealed class EmailConfirmationTokenService
{
    public const string InitialDelivery = "initial";
    public const string ReminderDelivery = "reminder";
    public const string ManualDelivery = "manual";
    public const string MigratedDelivery = "migrated";

    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UsedTokenRetention = TimeSpan.FromDays(7);

    private readonly string _connectionString;
    private readonly SecurityTokenService _tokens;
    private readonly TimeProvider _timeProvider;

    public EmailConfirmationTokenService(
        IConfiguration configuration,
        SecurityTokenService tokens,
        TimeProvider timeProvider)
    {
        _connectionString =
            configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        _tokens = tokens;
        _timeProvider = timeProvider;
    }

    public async Task<EmailConfirmationTokenIssue> CreateInitialTokenAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        string name,
        string email,
        CancellationToken cancellationToken = default) =>
        await InsertTokenAsync(
            db,
            transaction,
            userId,
            name,
            email,
            InitialDelivery,
            createClaim: false,
            cancellationToken);

    public async Task<EmailConfirmationPreparationResult> PrepareManualAsync(
        Guid userId,
        bool force,
        DateTime localNow,
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);

        ConfirmationUser? user =
            await db.QueryFirstOrDefaultAsync<ConfirmationUser>(
                new CommandDefinition(@"
                    SELECT id AS ""Id"", name AS ""Name"", email AS ""Email"",
                           email_confirmed AS ""EmailConfirmed"",
                           email_confirmation_last_sent_at AS ""LastSentAt""
                    FROM users
                    WHERE id = @UserId
                    FOR UPDATE;",
                    new { UserId = userId },
                    transaction,
                    cancellationToken: cancellationToken));

        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationPreparationStatus.NotFound);
        }
        if (user.EmailConfirmed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationPreparationStatus.AlreadyConfirmed);
        }
        if (!force && user.LastSentAt?.Date == localNow.Date)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                EmailConfirmationPreparationStatus.SameDayConfirmationRequired,
                LastSentAt: user.LastSentAt);
        }
        if (await HasActiveDeliveryClaimAsync(
                db,
                transaction,
                user.Id,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                EmailConfirmationPreparationStatus.DeliveryInProgress,
                LastSentAt: user.LastSentAt);
        }

        EmailConfirmationTokenIssue issue = await InsertTokenAsync(
            db,
            transaction,
            user.Id,
            user.Name,
            user.Email,
            ManualDelivery,
            createClaim: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(EmailConfirmationPreparationStatus.Prepared, issue);
    }

    public async Task<EmailConfirmationTokenIssue?> PrepareDueReminderAsync(
        DateTime localNow,
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);
        DateTime utcNow = UtcNow;

        ConfirmationUser? user =
            await db.QueryFirstOrDefaultAsync<ConfirmationUser>(
                new CommandDefinition(@"
                    SELECT u.id AS ""Id"", u.name AS ""Name"", u.email AS ""Email"",
                           u.email_confirmed AS ""EmailConfirmed"",
                           u.email_confirmation_last_sent_at AS ""LastSentAt""
                    FROM users u
                    WHERE u.email_confirmed = false
                      AND COALESCE(u.email_confirmation_resend_count, 0) < 2
                      AND u.email_confirmation_next_send_at IS NOT NULL
                      AND u.email_confirmation_next_send_at <= @LocalNow
                      AND EXISTS (
                          SELECT 1
                          FROM email_confirmation_tokens active_token
                          WHERE active_token.user_id = u.id
                            AND active_token.used_at IS NULL
                            AND active_token.expires_at > @UtcNow
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM email_confirmation_tokens pending_delivery
                          WHERE pending_delivery.user_id = u.id
                            AND pending_delivery.sent_at IS NULL
                            AND pending_delivery.failed_at IS NULL
                            AND pending_delivery.claim_expires_at > @UtcNow
                      )
                    ORDER BY u.email_confirmation_next_send_at, u.id
                    LIMIT 1
                    FOR UPDATE OF u SKIP LOCKED;",
                    new { LocalNow = localNow, UtcNow = utcNow },
                    transaction,
                    cancellationToken: cancellationToken));

        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        EmailConfirmationTokenIssue issue = await InsertTokenAsync(
            db,
            transaction,
            user.Id,
            user.Name,
            user.Email,
            ReminderDelivery,
            createClaim: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return issue;
    }

    public async Task<EmailConfirmationDeliveryResult> MarkSentAsync(
        long tokenId,
        DateTime localNow,
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);

        DeliveryRow? delivery =
            await db.QueryFirstOrDefaultAsync<DeliveryRow>(
                new CommandDefinition(@"
                    SELECT t.id AS ""TokenId"", t.user_id AS ""UserId"",
                           t.delivery_kind AS ""DeliveryKind"",
                           t.sent_at AS ""SentAt"", t.used_at AS ""UsedAt"",
                           u.email_confirmed AS ""EmailConfirmed"",
                           COALESCE(u.email_confirmation_resend_count, 0)
                               AS ""ReminderCount"",
                           u.created_at AS ""UserCreatedAt""
                    FROM email_confirmation_tokens t
                    INNER JOIN users u ON u.id = t.user_id
                    WHERE t.id = @TokenId
                    FOR UPDATE OF t, u;",
                    new { TokenId = tokenId },
                    transaction,
                    cancellationToken: cancellationToken));

        if (delivery == null || delivery.SentAt.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false);
        }

        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE email_confirmation_tokens
            SET sent_at = @UtcNow,
                claim_expires_at = NULL
            WHERE id = @TokenId;",
            new { TokenId = tokenId, UtcNow },
            transaction,
            cancellationToken: cancellationToken));

        int? nextCount = null;
        DateTime? nextSendAt = null;
        if (!delivery.EmailConfirmed && !delivery.UsedAt.HasValue)
        {
            if (delivery.DeliveryKind == ReminderDelivery)
            {
                nextCount = delivery.ReminderCount + 1;
                DateTime secondReminderAt =
                    delivery.UserCreatedAt.Date.AddDays(2).AddHours(19);
                if (secondReminderAt.Date <= localNow.Date)
                    secondReminderAt = localNow.Date.AddDays(1).AddHours(19);
                nextSendAt = nextCount < 2 ? secondReminderAt : null;

                await db.ExecuteAsync(new CommandDefinition(@"
                    UPDATE users
                    SET email_confirmation_resend_count = @NextCount,
                        email_confirmation_last_sent_at = @LocalNow,
                        email_confirmation_next_send_at = @NextSendAt
                    WHERE id = @UserId AND email_confirmed = false;",
                    new
                    {
                        NextCount = nextCount,
                        LocalNow = localNow,
                        NextSendAt = nextSendAt,
                        delivery.UserId
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }
            else
            {
                await db.ExecuteAsync(new CommandDefinition(@"
                    UPDATE users
                    SET email_confirmation_last_sent_at = @LocalNow
                    WHERE id = @UserId AND email_confirmed = false;",
                    new { LocalNow = localNow, delivery.UserId },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(true, delivery.UserId, nextCount, nextSendAt);
    }

    public async Task MarkFailedAsync(
        long tokenId,
        string sanitizedFailureCode,
        DateTime? reminderRetryAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);

        DeliveryIdentity? delivery =
            await db.QueryFirstOrDefaultAsync<DeliveryIdentity>(
                new CommandDefinition(@"
                    UPDATE email_confirmation_tokens
                    SET failed_at = COALESCE(failed_at, @UtcNow),
                        sanitized_failure_code =
                            COALESCE(sanitized_failure_code, @FailureCode),
                        claim_expires_at = NULL
                    WHERE id = @TokenId
                      AND sent_at IS NULL
                    RETURNING user_id AS ""UserId"",
                              delivery_kind AS ""DeliveryKind"";",
                    new
                    {
                        TokenId = tokenId,
                        UtcNow,
                        FailureCode = NormalizeFailureCode(
                            sanitizedFailureCode)
                    },
                    transaction,
                    cancellationToken: cancellationToken));

        if (delivery != null &&
            delivery.DeliveryKind == ReminderDelivery &&
            reminderRetryAt.HasValue)
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE users
                SET email_confirmation_next_send_at = @RetryAt
                WHERE id = @UserId
                  AND email_confirmed = false
                  AND COALESCE(email_confirmation_resend_count, 0) < 2;",
                new
                {
                    RetryAt = reminderRetryAt.Value,
                    delivery.UserId
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<EmailConfirmationResult> ConfirmByTokenAsync(
        string? rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryHashToken(
                rawToken,
                SecurityTokenPurpose.EmailConfirmation,
                out string tokenHash))
        {
            return new(EmailConfirmationResultStatus.InvalidOrUsed);
        }

        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);
        DateTime utcNow = UtcNow;

        ConfirmationTokenUser? user =
            await db.QueryFirstOrDefaultAsync<ConfirmationTokenUser>(
                new CommandDefinition(@"
                    SELECT u.id AS ""Id"", u.name AS ""Name"",
                           u.email AS ""Email""
                    FROM email_confirmation_tokens t
                    INNER JOIN users u ON u.id = t.user_id
                    WHERE t.token_hash = @TokenHash
                      AND t.used_at IS NULL
                      AND t.expires_at > @UtcNow
                      AND u.email_confirmed = false
                    FOR UPDATE OF u, t;",
                    new { TokenHash = tokenHash, UtcNow = utcNow },
                    transaction,
                    cancellationToken: cancellationToken));

        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationResultStatus.InvalidOrUsed);
        }

        int confirmed = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE users
            SET email_confirmed = true,
                email_confirmation_token = NULL,
                email_confirmation_next_send_at = NULL
            WHERE id = @UserId AND email_confirmed = false;",
            new { UserId = user.Id },
            transaction,
            cancellationToken: cancellationToken));
        if (confirmed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationResultStatus.InvalidOrUsed);
        }

        await InvalidateAllAsync(
            db,
            transaction,
            user.Id,
            utcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            EmailConfirmationResultStatus.Confirmed,
            user.Id,
            user.Name,
            user.Email);
    }

    public async Task<EmailConfirmationResult> ConfirmUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction =
            await db.BeginTransactionAsync(cancellationToken);

        ConfirmationUser? user =
            await db.QueryFirstOrDefaultAsync<ConfirmationUser>(
                new CommandDefinition(@"
                    SELECT id AS ""Id"", name AS ""Name"", email AS ""Email"",
                           email_confirmed AS ""EmailConfirmed""
                    FROM users
                    WHERE id = @UserId
                    FOR UPDATE;",
                    new { UserId = userId },
                    transaction,
                    cancellationToken: cancellationToken));
        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationResultStatus.NotFound);
        }
        if (user.EmailConfirmed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(EmailConfirmationResultStatus.AlreadyConfirmed);
        }

        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE users
            SET email_confirmed = true,
                email_confirmation_token = NULL,
                email_confirmation_next_send_at = NULL
            WHERE id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
        await InvalidateAllAsync(
            db,
            transaction,
            userId,
            UtcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            EmailConfirmationResultStatus.Confirmed,
            user.Id,
            user.Name,
            user.Email);
    }

    public async Task<EmailConfirmationCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default)
    {
        DateTime utcNow = UtcNow;
        await using var db = new NpgsqlConnection(_connectionString);
        int recovered = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE email_confirmation_tokens
            SET failed_at = @UtcNow,
                sanitized_failure_code = 'claim_expired',
                claim_expires_at = NULL
            WHERE sent_at IS NULL
              AND failed_at IS NULL
              AND claim_expires_at <= @UtcNow;",
            new { UtcNow = utcNow },
            cancellationToken: cancellationToken));
        int deleted = await db.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM email_confirmation_tokens
            WHERE (used_at IS NOT NULL AND used_at < @RetentionCutoff)
               OR (expires_at < @RetentionCutoff);",
            new { RetentionCutoff = utcNow.Subtract(UsedTokenRetention) },
            cancellationToken: cancellationToken));
        return new(recovered, deleted);
    }

    private async Task<EmailConfirmationTokenIssue> InsertTokenAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        string name,
        string email,
        string deliveryKind,
        bool createClaim,
        CancellationToken cancellationToken)
    {
        string rawToken = _tokens.GenerateToken();
        string tokenHash = _tokens.HashToken(rawToken);
        DateTime utcNow = UtcNow;
        long tokenId = await db.QuerySingleAsync<long>(
            new CommandDefinition(@"
                INSERT INTO email_confirmation_tokens
                    (user_id, token_hash, created_at, expires_at,
                     delivery_kind, claim_expires_at)
                VALUES
                    (@UserId, @TokenHash, @UtcNow, 'infinity'::timestamp,
                     @DeliveryKind, @ClaimExpiresAt)
                RETURNING id;",
                new
                {
                    UserId = userId,
                    TokenHash = tokenHash,
                    UtcNow = utcNow,
                    DeliveryKind = deliveryKind,
                    ClaimExpiresAt =
                        createClaim ? utcNow.Add(ClaimLifetime) : (DateTime?)null
                },
                transaction,
                cancellationToken: cancellationToken));
        return new(
            tokenId,
            userId,
            rawToken,
            name,
            email,
            deliveryKind);
    }

    private async Task<bool> HasActiveDeliveryClaimAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken) =>
        await db.QuerySingleAsync<bool>(
            new CommandDefinition(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM email_confirmation_tokens
                    WHERE user_id = @UserId
                      AND sent_at IS NULL
                      AND failed_at IS NULL
                      AND claim_expires_at > @UtcNow
                );",
                new { UserId = userId, UtcNow },
                transaction,
                cancellationToken: cancellationToken));

    private static Task InvalidateAllAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        DateTime usedAt,
        CancellationToken cancellationToken) =>
        db.ExecuteAsync(new CommandDefinition(@"
            UPDATE email_confirmation_tokens
            SET used_at = COALESCE(used_at, @UsedAt),
                claim_expires_at = NULL
            WHERE user_id = @UserId
              AND used_at IS NULL;",
            new { UserId = userId, UsedAt = usedAt },
            transaction,
            cancellationToken: cancellationToken));

    private static string NormalizeFailureCode(string failureCode)
    {
        string normalized = (failureCode ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        return normalized is "smtp_timeout" or "smtp_canceled" or
            "smtp_failure" or "claim_expired"
            ? normalized
            : "smtp_failure";
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed class ConfirmationUser
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
        public bool EmailConfirmed { get; init; }
        public DateTime? LastSentAt { get; init; }
    }

    private sealed class ConfirmationTokenUser
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
    }

    private sealed class DeliveryRow
    {
        public long TokenId { get; init; }
        public Guid UserId { get; init; }
        public string DeliveryKind { get; init; } = "";
        public DateTime? SentAt { get; init; }
        public DateTime? UsedAt { get; init; }
        public bool EmailConfirmed { get; init; }
        public int ReminderCount { get; init; }
        public DateTime UserCreatedAt { get; init; }
    }

    private sealed class DeliveryIdentity
    {
        public Guid UserId { get; init; }
        public string DeliveryKind { get; init; } = "";
    }
}
