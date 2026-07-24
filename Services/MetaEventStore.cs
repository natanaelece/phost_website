using Dapper;
using Npgsql;

namespace PremierAPI.Services;

public interface IMetaEventStore
{
    Task<bool> TryBeginAsync(string eventId, string eventName, CancellationToken cancellationToken);
    Task MarkSucceededAsync(string eventId, CancellationToken cancellationToken);
    Task MarkFailedAsync(string eventId, CancellationToken cancellationToken);
}

public sealed class PostgresMetaEventStore : IMetaEventStore
{
    private readonly string _connectionString;

    public PostgresMetaEventStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
    }

    public async Task<bool> TryBeginAsync(
        string eventId,
        string eventName,
        CancellationToken cancellationToken)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        return await db.QuerySingleOrDefaultAsync<bool>(new CommandDefinition(@"
            INSERT INTO meta_conversion_events
                (event_id, event_name, delivery_status, attempt_count, last_attempt_at)
            VALUES
                (@EventId, @EventName, 'processing', 1, CURRENT_TIMESTAMP)
            ON CONFLICT (event_id) DO UPDATE
            SET delivery_status = 'processing',
                attempt_count = meta_conversion_events.attempt_count + 1,
                last_attempt_at = CURRENT_TIMESTAMP
            WHERE meta_conversion_events.delivery_status = 'failed'
               OR (
                    meta_conversion_events.delivery_status = 'processing'
                    AND meta_conversion_events.last_attempt_at < CURRENT_TIMESTAMP - INTERVAL '5 minutes'
               )
            RETURNING true;",
            new { EventId = eventId, EventName = eventName },
            cancellationToken: cancellationToken));
    }

    public Task MarkSucceededAsync(string eventId, CancellationToken cancellationToken) =>
        UpdateStatusAsync(eventId, "sent", cancellationToken);

    public Task MarkFailedAsync(string eventId, CancellationToken cancellationToken) =>
        UpdateStatusAsync(eventId, "failed", cancellationToken);

    private async Task UpdateStatusAsync(
        string eventId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE meta_conversion_events
            SET delivery_status = @Status,
                sent_at = CASE WHEN @Status = 'sent' THEN CURRENT_TIMESTAMP ELSE sent_at END
            WHERE event_id = @EventId;",
            new { EventId = eventId, Status = status },
            cancellationToken: cancellationToken));
    }
}
