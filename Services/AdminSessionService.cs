using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PremierAPI.Services;

public sealed class AdminSessionService
{
    public const string CookieName = "premier_admin_session";

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _sessionLifetime;

    public AdminSessionService(IConfiguration configuration)
    {
        int sessionHours = configuration.GetValue<int>("AdminSecurity:SessionHours");
        _sessionLifetime = TimeSpan.FromHours(sessionHours);
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

    private void RemoveExpiredSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAt <= now)
                _sessions.TryRemove(item.Key, out _);
        }
    }
}

public sealed record AdminSession(string CsrfToken, DateTimeOffset ExpiresAt);
public sealed record CreatedAdminSession(string Token, string CsrfToken, DateTimeOffset ExpiresAt);
