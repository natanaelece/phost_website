using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PremierAPI.Services;

public sealed class AdminSessionService
{
    public const string CookieName = "premier_admin_session";

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AdminLoginChallenge> _loginChallenges = new(StringComparer.Ordinal);
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeSpan _challengeLifetime;

    public AdminSessionService(IConfiguration configuration)
    {
        int sessionHours = configuration.GetValue<int>("AdminSecurity:SessionHours");
        int challengeMinutes = configuration.GetValue<int>("AdminSecurity:LoginChallengeMinutes");
        _sessionLifetime = TimeSpan.FromHours(sessionHours);
        _challengeLifetime = TimeSpan.FromMinutes(challengeMinutes);
    }

    public CreatedAdminSession CreateSession()
    {
        RemoveExpiredSessions();

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(_sessionLifetime);
        _sessions[token] = new AdminSession(csrfToken, expiresAt);
        return new CreatedAdminSession(token, csrfToken, expiresAt);
    }

    public bool TryValidate(string? token, out AdminSession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var stored))
            return false;

        if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        session = stored;
        return true;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            _sessions.TryRemove(token, out _);
    }

    public CreatedAdminLoginChallenge CreateLoginChallenge(string? pendingSetupSecret)
    {
        RemoveExpiredChallenges();
        string challengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(_challengeLifetime);
        _loginChallenges[challengeId] = new AdminLoginChallenge(pendingSetupSecret, expiresAt);
        return new CreatedAdminLoginChallenge(challengeId, expiresAt);
    }

    public AdminChallengeValidationResult ValidateLoginChallenge(
        string? challengeId,
        Func<AdminLoginChallenge, bool> validator,
        out AdminLoginChallenge? challenge)
    {
        challenge = null;
        if (string.IsNullOrWhiteSpace(challengeId) ||
            !_loginChallenges.TryGetValue(challengeId, out var stored))
        {
            return AdminChallengeValidationResult.Expired;
        }

        lock (stored)
        {
            if (stored.Completed || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _loginChallenges.TryRemove(challengeId, out _);
                return AdminChallengeValidationResult.Expired;
            }

            bool valid = validator(stored);
            if (!valid)
            {
                stored.Attempts++;
                if (stored.Attempts >= 5)
                    _loginChallenges.TryRemove(challengeId, out _);
                return stored.Attempts >= 5
                    ? AdminChallengeValidationResult.Expired
                    : AdminChallengeValidationResult.Invalid;
            }

            stored.Completed = true;
            _loginChallenges.TryRemove(challengeId, out _);
            challenge = stored;
            return AdminChallengeValidationResult.Success;
        }
    }

    private void RemoveExpiredSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAt <= now)
                _sessions.TryRemove(item.Key, out _);
        }
    }

    private void RemoveExpiredChallenges()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var item in _loginChallenges)
        {
            if (item.Value.Completed || item.Value.ExpiresAt <= now)
                _loginChallenges.TryRemove(item.Key, out _);
        }
    }
}

public sealed record AdminSession(string CsrfToken, DateTimeOffset ExpiresAt);
public sealed record CreatedAdminSession(string Token, string CsrfToken, DateTimeOffset ExpiresAt);
public sealed record CreatedAdminLoginChallenge(string ChallengeId, DateTimeOffset ExpiresAt);

public sealed class AdminLoginChallenge
{
    public AdminLoginChallenge(string? pendingSetupSecret, DateTimeOffset expiresAt)
    {
        PendingSetupSecret = pendingSetupSecret;
        ExpiresAt = expiresAt;
    }

    public string? PendingSetupSecret { get; }
    public DateTimeOffset ExpiresAt { get; }
    public int Attempts { get; set; }
    public bool Completed { get; set; }
    public bool IsSetup => !string.IsNullOrWhiteSpace(PendingSetupSecret);
}

public enum AdminChallengeValidationResult
{
    Success,
    Invalid,
    Expired
}
