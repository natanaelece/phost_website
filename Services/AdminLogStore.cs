using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PremierAPI.Services;

public sealed record AdminLogEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);

public sealed class AdminLogStore
{
    private const int Capacity = 2_000;
    private const int MaxTextLength = 8_000;
    private static readonly Regex SensitiveValuePattern = new(
        @"(?i)\b(authorization|access[_-]?token|api[_-]?key|password|senha|token)\b\s*[:=]\s*([^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentQueue<AdminLogEntry> _entries = new();
    private long _nextId;
    private int _count;

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var entry = new AdminLogEntry(
            Interlocked.Increment(ref _nextId),
            DateTimeOffset.Now,
            level.ToString(),
            category,
            Sanitize(message),
            exception == null ? null : Sanitize(exception.ToString()));

        _entries.Enqueue(entry);
        Interlocked.Increment(ref _count);
        while (Volatile.Read(ref _count) > Capacity && _entries.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    public IReadOnlyList<AdminLogEntry> Query(string? level, string? search, int limit)
    {
        limit = Math.Clamp(limit, 1, 1_000);
        IEnumerable<AdminLogEntry> query = _entries.ToArray().Reverse();

        if (!string.IsNullOrWhiteSpace(level) && !level.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(entry => entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            query = query.Where(entry =>
                entry.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (entry.Exception?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query.Take(limit).ToArray();
    }

    private static string Sanitize(string value)
    {
        string sanitized = BearerPattern.Replace(value ?? string.Empty, "Bearer [PROTEGIDO]");
        sanitized = SensitiveValuePattern.Replace(sanitized, "$1=[PROTEGIDO]");
        return sanitized.Length <= MaxTextLength ? sanitized : sanitized[..MaxTextLength] + "…";
    }
}

public sealed class AdminLogProvider(AdminLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AdminLogLogger(categoryName, store);
    public void Dispose() { }

    private sealed class AdminLogLogger(string category, AdminLogStore store) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            store.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
