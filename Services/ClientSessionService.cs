using System.Data;
using Dapper;
using Npgsql;

namespace PremierAPI.Services;

public sealed record ClientSessionIssue(string Token, DateTime ExpiresAt);

public sealed record ClientSessionActivity(
    string? IpAddress,
    string? UserAgent,
    string? AcceptLanguage,
    string? CountryCode,
    string? ReferrerHost);

public sealed class ClientSessionService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    private readonly string _connectionString;
    private readonly SecurityTokenService _tokens;
    private readonly TimeProvider _timeProvider;

    public ClientSessionService(
        IConfiguration configuration,
        SecurityTokenService tokens,
        TimeProvider timeProvider)
    {
        _connectionString =
            configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        _tokens = tokens;
        _timeProvider = timeProvider;
    }

    public async Task<Guid?> FindUserIdAsync(
        string? rawToken,
        Guid? expectedUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryHashToken(
                rawToken,
                SecurityTokenPurpose.ClientSession,
                out string tokenHash))
        {
            return null;
        }

        await using var db = new NpgsqlConnection(_connectionString);
        return await FindUserIdByHashAsync(
            db,
            transaction: null,
            tokenHash,
            expectedUserId,
            lockUser: false,
            cancellationToken);
    }

    public async Task<Guid?> FindUserIdAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        string? rawToken,
        Guid? expectedUserId,
        bool lockUser,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryHashToken(
                rawToken,
                SecurityTokenPurpose.ClientSession,
                out string tokenHash))
        {
            return null;
        }

        return await FindUserIdByHashAsync(
            db,
            transaction,
            tokenHash,
            expectedUserId,
            lockUser,
            cancellationToken);
    }

    public async Task<ClientSessionIssue> CreateSessionAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        string rawToken = _tokens.GenerateToken();
        string tokenHash = _tokens.HashToken(rawToken);
        DateTime expiresAt = _timeProvider.GetUtcNow().UtcDateTime.Add(SessionLifetime);

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO user_sessions (user_id, token_hash, expires_at)
            VALUES (@UserId, @TokenHash, @ExpiresAt);",
            new { UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt },
            transaction,
            cancellationToken: cancellationToken));

        return new ClientSessionIssue(rawToken, expiresAt);
    }

    public Task<int> RevokeAllSessionsAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_sessions WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

    public async Task<ClientSessionIssue> RotateSessionAsync(
        NpgsqlConnection db,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await RevokeAllSessionsAsync(
            db,
            transaction,
            userId,
            cancellationToken);
        return await CreateSessionAsync(
            db,
            transaction,
            userId,
            cancellationToken);
    }

    public async Task<Guid?> LogoutAsync(
        string? rawToken,
        ClientSessionActivity activity,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryHashToken(
                rawToken,
                SecurityTokenPurpose.ClientSession,
                out string tokenHash))
        {
            return null;
        }

        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);

        Guid? userId = await db.QueryFirstOrDefaultAsync<Guid?>(
            new CommandDefinition(@"
                SELECT user_id
                FROM user_sessions
                WHERE token_hash = @TokenHash
                FOR UPDATE;",
                new { TokenHash = tokenHash },
                transaction,
                cancellationToken: cancellationToken));

        if (!userId.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO user_activity_events
                (user_id, event_type, ip_address, user_agent, accept_language,
                 country_code, referrer_host)
            VALUES
                (@UserId, 'logout', CAST(@IpAddress AS inet), @UserAgent,
                 @AcceptLanguage, @CountryCode, @ReferrerHost);",
            new
            {
                UserId = userId.Value,
                activity.IpAddress,
                activity.UserAgent,
                activity.AcceptLanguage,
                activity.CountryCode,
                activity.ReferrerHost
            },
            transaction,
            cancellationToken: cancellationToken));
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_sessions WHERE token_hash = @TokenHash;",
            new { TokenHash = tokenHash },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    public async Task<int> DeleteExpiredSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_sessions WHERE expires_at <= @Now;",
            new { Now = _timeProvider.GetUtcNow().UtcDateTime },
            cancellationToken: cancellationToken));
    }

    private async Task<Guid?> FindUserIdByHashAsync(
        NpgsqlConnection db,
        NpgsqlTransaction? transaction,
        string tokenHash,
        Guid? expectedUserId,
        bool lockUser,
        CancellationToken cancellationToken)
    {
        string lockClause = lockUser ? "FOR UPDATE OF u" : string.Empty;
        return await db.QueryFirstOrDefaultAsync<Guid?>(
            new CommandDefinition($@"
                SELECT u.id
                FROM user_sessions s
                INNER JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @TokenHash
                  AND (@ExpectedUserId IS NULL OR s.user_id = @ExpectedUserId)
                  AND s.expires_at > @Now
                  AND u.is_active = true
                LIMIT 1
                {lockClause};",
                new
                {
                    TokenHash = tokenHash,
                    ExpectedUserId = expectedUserId,
                    Now = _timeProvider.GetUtcNow().UtcDateTime
                },
                transaction,
                cancellationToken: cancellationToken));
    }
}
