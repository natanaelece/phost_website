using Dapper;
using Npgsql;

namespace PremierAPI.Services
{
    public sealed class FreeTrialService
    {
        private static readonly HashSet<string> ClosedStatuses = new(StringComparer.Ordinal)
        {
            "recusado", "cancelado"
        };

        private readonly string _connectionString;

        public FreeTrialService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        public async Task<FreeTrialStatusDto?> GetMineAsync(string? sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return null;

            await using var db = new NpgsqlConnection(_connectionString);
            var row = await db.QueryFirstOrDefaultAsync<FreeTrialUserRow>(@"
                SELECT
                    u.id AS ""UserId"",
                    f.id AS ""RequestId"",
                    f.status AS ""Status"",
                    f.first_requested_at AS ""FirstRequestedAt"",
                    f.last_requested_at AS ""LastRequestedAt"",
                    f.released_at AS ""ReleasedAt"",
                    f.used_at AS ""UsedAt"",
                    f.closed_at AS ""ClosedAt"",
                    COALESCE(f.request_count, 0) AS ""RequestCount""
                FROM user_sessions s
                INNER JOIN users u ON u.id = s.user_id
                LEFT JOIN free_trial_requests f ON f.user_id = u.id
                WHERE s.token = @Token
                  AND s.expires_at > @Now
                  AND u.is_active = true
                LIMIT 1;",
                new { Token = sessionToken, Now = DateTime.UtcNow });

            return row == null ? null : ToStatus(row);
        }

        public async Task<FreeTrialRequestResult?> RequestAsync(string? sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return null;

            await using var db = new NpgsqlConnection(_connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();

            Guid? userId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                SELECT u.id
                FROM user_sessions s
                INNER JOIN users u ON u.id = s.user_id
                WHERE s.token = @Token
                  AND s.expires_at > @Now
                  AND u.is_active = true
                LIMIT 1;",
                new { Token = sessionToken, Now = DateTime.UtcNow }, transaction);

            if (!userId.HasValue)
            {
                await transaction.RollbackAsync();
                return null;
            }

            Guid? insertedId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                INSERT INTO free_trial_requests (user_id)
                VALUES (@UserId)
                ON CONFLICT (user_id) DO NOTHING
                RETURNING id;",
                new { UserId = userId.Value }, transaction);

            if (insertedId.HasValue)
            {
                await InsertEventAsync(db, transaction, insertedId.Value, "solicitado", "usuario", userId.Value.ToString());
                var created = await GetRequestRowAsync(db, transaction, userId.Value);
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(created!), true, false);
            }

            var current = await db.QueryFirstAsync<FreeTrialUserRow>(@"
                SELECT
                    user_id AS ""UserId"",
                    id AS ""RequestId"",
                    status AS ""Status"",
                    first_requested_at AS ""FirstRequestedAt"",
                    last_requested_at AS ""LastRequestedAt"",
                    released_at AS ""ReleasedAt"",
                    used_at AS ""UsedAt"",
                    closed_at AS ""ClosedAt"",
                    request_count AS ""RequestCount""
                FROM free_trial_requests
                WHERE user_id = @UserId
                FOR UPDATE;",
                new { UserId = userId.Value }, transaction);

            if (current.UsedAt.HasValue || string.Equals(current.Status, "utilizado", StringComparison.Ordinal))
            {
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(current), false, true);
            }

            if (!ClosedStatuses.Contains(current.Status ?? ""))
            {
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(current), false, false);
            }

            await db.ExecuteAsync(@"
                UPDATE free_trial_requests
                SET status = 'solicitado',
                    last_requested_at = CURRENT_TIMESTAMP,
                    request_count = request_count + 1,
                    released_at = NULL,
                    released_by = NULL,
                    closed_at = NULL,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @Id;",
                new { Id = current.RequestId }, transaction);
            await InsertEventAsync(db, transaction, current.RequestId!.Value, "solicitado", "usuario", userId.Value.ToString());

            var reopened = await GetRequestRowAsync(db, transaction, userId.Value);
            await transaction.CommitAsync();
            return new FreeTrialRequestResult(ToStatus(reopened!), false, false);
        }

        public async Task<FreeTrialAdminListDto> GetAdminListAsync(
            string filter, int page, int limit, string search, string sortBy, string sortDir)
        {
            page = Math.Max(1, page);
            limit = Math.Clamp(limit, 1, 100);
            filter = (filter ?? "all").Trim().ToLowerInvariant();
            search = (search ?? "").Trim();

            string filterSql = filter switch
            {
                "never_requested" => "f.id IS NULL",
                "not_used" => "f.used_at IS NULL",
                "solicitado" => "f.status = 'solicitado'",
                "liberado" => "f.status = 'liberado'",
                "utilizado" => "f.status = 'utilizado'",
                "recusado" => "f.status = 'recusado'",
                "cancelado" => "f.status = 'cancelado'",
                _ => "TRUE"
            };

            var orderColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "u.name",
                ["whatsapp"] = "u.whatsapp",
                ["status"] = "f.status",
                ["firstRequestedAt"] = "f.first_requested_at",
                ["lastRequestedAt"] = "f.last_requested_at",
                ["releasedAt"] = "f.released_at",
                ["usedAt"] = "f.used_at",
                ["createdAt"] = "u.created_at"
            };
            if (!orderColumns.TryGetValue(sortBy ?? "", out string? orderColumn)) orderColumn = "f.last_requested_at";
            string direction = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            string searchSql = string.IsNullOrWhiteSpace(search)
                ? "TRUE"
                : "(u.name ILIKE @Search OR u.email ILIKE @Search OR COALESCE(u.whatsapp, '') ILIKE @Search)";
            int offset = (page - 1) * limit;

            await using var db = new NpgsqlConnection(_connectionString);
            long total = await db.QuerySingleAsync<long>($@"
                SELECT COUNT(*)
                FROM users u
                LEFT JOIN free_trial_requests f ON f.user_id = u.id
                WHERE {filterSql} AND {searchSql};",
                new { Search = $"%{search}%" });

            var users = await db.QueryAsync<FreeTrialAdminUserDto>($@"
                SELECT
                    u.id AS ""UserId"",
                    u.name AS ""Name"",
                    u.email AS ""Email"",
                    u.whatsapp AS ""Whatsapp"",
                    u.created_at AS ""CreatedAt"",
                    u.registration_ip::text AS ""RegistrationIp"",
                    u.registration_user_agent AS ""RegistrationUserAgent"",
                    u.registration_accept_language AS ""RegistrationAcceptLanguage"",
                    u.registration_country_code AS ""RegistrationCountryCode"",
                    u.registration_referrer_host AS ""RegistrationReferrerHost"",
                    u.registration_source AS ""RegistrationSource"",
                    f.id AS ""RequestId"",
                    COALESCE(f.status, 'nao_solicitado') AS ""Status"",
                    f.first_requested_at AS ""FirstRequestedAt"",
                    f.last_requested_at AS ""LastRequestedAt"",
                    f.released_at AS ""ReleasedAt"",
                    f.used_at AS ""UsedAt"",
                    f.closed_at AS ""ClosedAt"",
                    COALESCE(f.request_count, 0) AS ""RequestCount"",
                    f.released_by AS ""ReleasedBy""
                FROM users u
                LEFT JOIN free_trial_requests f ON f.user_id = u.id
                WHERE {filterSql} AND {searchSql}
                ORDER BY {orderColumn} {direction} NULLS LAST, u.id DESC
                LIMIT @Limit OFFSET @Offset;",
                new { Search = $"%{search}%", Limit = limit, Offset = offset });

            var stats = await db.QuerySingleAsync<FreeTrialAdminStatsDto>(@"
                SELECT
                    COUNT(*) FILTER (WHERE f.id IS NULL) AS ""NeverRequested"",
                    COUNT(*) FILTER (WHERE f.used_at IS NULL) AS ""NotUsed"",
                    COUNT(*) FILTER (WHERE f.status = 'solicitado') AS ""Requested"",
                    COUNT(*) FILTER (WHERE f.status = 'liberado') AS ""Released"",
                    COUNT(*) FILTER (WHERE f.status = 'utilizado') AS ""Used""
                FROM users u
                LEFT JOIN free_trial_requests f ON f.user_id = u.id;");

            return new FreeTrialAdminListDto(total, page, limit, users.ToArray(), stats);
        }

        public async Task<FreeTrialAdminTransitionResult?> TransitionAsync(Guid requestId, string action, string actor)
        {
            string targetStatus = action switch
            {
                "release" => "liberado",
                "mark-used" => "utilizado",
                "reject" => "recusado",
                "cancel" => "cancelado",
                _ => ""
            };
            if (string.IsNullOrEmpty(targetStatus)) return new FreeTrialAdminTransitionResult(false, "Ação inválida.", null);

            await using var db = new NpgsqlConnection(_connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            var current = await db.QueryFirstOrDefaultAsync<FreeTrialUserRow>(@"
                SELECT
                    user_id AS ""UserId"", id AS ""RequestId"", status AS ""Status"",
                    first_requested_at AS ""FirstRequestedAt"", last_requested_at AS ""LastRequestedAt"",
                    released_at AS ""ReleasedAt"", used_at AS ""UsedAt"", closed_at AS ""ClosedAt"",
                    request_count AS ""RequestCount""
                FROM free_trial_requests
                WHERE id = @Id
                FOR UPDATE;",
                new { Id = requestId }, transaction);

            if (current == null)
            {
                await transaction.RollbackAsync();
                return null;
            }
            if (string.Equals(current.Status, targetStatus, StringComparison.Ordinal))
            {
                await transaction.CommitAsync();
                return new FreeTrialAdminTransitionResult(true, "Estado já estava atualizado.", ToStatus(current));
            }

            bool allowed = action switch
            {
                "release" => current.Status == "solicitado",
                "mark-used" => current.Status == "liberado",
                "reject" => current.Status == "solicitado",
                "cancel" => current.Status is "solicitado" or "liberado",
                _ => false
            };
            if (!allowed)
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminTransitionResult(false, "Transição incompatível com o estado atual.", ToStatus(current));
            }

            await db.ExecuteAsync(@"
                UPDATE free_trial_requests
                SET status = @Status,
                    released_at = CASE WHEN @Status = 'liberado' THEN CURRENT_TIMESTAMP ELSE released_at END,
                    released_by = CASE WHEN @Status = 'liberado' THEN @Actor ELSE released_by END,
                    used_at = CASE WHEN @Status = 'utilizado' THEN CURRENT_TIMESTAMP ELSE used_at END,
                    closed_at = CASE WHEN @Status IN ('recusado', 'cancelado') THEN CURRENT_TIMESTAMP ELSE closed_at END,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @Id;",
                new { Status = targetStatus, Actor = actor, Id = requestId }, transaction);
            await InsertEventAsync(db, transaction, requestId, targetStatus, "admin", actor);

            var updated = await GetRequestRowAsync(db, transaction, current.UserId);
            await transaction.CommitAsync();
            return new FreeTrialAdminTransitionResult(true, "Situação atualizada.", ToStatus(updated!));
        }

        private static async Task InsertEventAsync(
            NpgsqlConnection db, NpgsqlTransaction transaction, Guid requestId,
            string eventType, string actorType, string actorIdentifier)
        {
            await db.ExecuteAsync(@"
                INSERT INTO free_trial_events
                    (free_trial_request_id, event_type, actor_type, actor_identifier)
                VALUES
                    (@RequestId, @EventType, @ActorType, @ActorIdentifier);",
                new { RequestId = requestId, EventType = eventType, ActorType = actorType, ActorIdentifier = actorIdentifier },
                transaction);
        }

        private static Task<FreeTrialUserRow?> GetRequestRowAsync(
            NpgsqlConnection db, NpgsqlTransaction transaction, Guid userId)
        {
            return db.QueryFirstOrDefaultAsync<FreeTrialUserRow>(@"
                SELECT
                    user_id AS ""UserId"", id AS ""RequestId"", status AS ""Status"",
                    first_requested_at AS ""FirstRequestedAt"", last_requested_at AS ""LastRequestedAt"",
                    released_at AS ""ReleasedAt"", used_at AS ""UsedAt"", closed_at AS ""ClosedAt"",
                    request_count AS ""RequestCount""
                FROM free_trial_requests
                WHERE user_id = @UserId;",
                new { UserId = userId }, transaction);
        }

        private static FreeTrialStatusDto ToStatus(FreeTrialUserRow row)
        {
            string status = string.IsNullOrWhiteSpace(row.Status) ? "nao_solicitado" : row.Status;
            bool hasUsed = row.UsedAt.HasValue || status == "utilizado";
            bool canRequest = (status is "nao_solicitado" or "recusado" or "cancelado") && !hasUsed;
            return new FreeTrialStatusDto(
                row.RequestId, status, canRequest, hasUsed,
                row.FirstRequestedAt, row.LastRequestedAt, row.ReleasedAt, row.UsedAt,
                row.ClosedAt, row.RequestCount);
        }

        private sealed class FreeTrialUserRow
        {
            public Guid UserId { get; set; }
            public Guid? RequestId { get; set; }
            public string? Status { get; set; }
            public DateTime? FirstRequestedAt { get; set; }
            public DateTime? LastRequestedAt { get; set; }
            public DateTime? ReleasedAt { get; set; }
            public DateTime? UsedAt { get; set; }
            public DateTime? ClosedAt { get; set; }
            public int RequestCount { get; set; }
        }
    }

    public sealed record FreeTrialStatusDto(
        Guid? RequestId, string Status, bool CanRequest, bool HasUsed,
        DateTime? FirstRequestedAt, DateTime? LastRequestedAt, DateTime? ReleasedAt,
        DateTime? UsedAt, DateTime? ClosedAt, int RequestCount);

    public sealed record FreeTrialRequestResult(FreeTrialStatusDto Status, bool Created, bool AlreadyUsed);

    public sealed record FreeTrialAdminUserDto(
        Guid UserId, string Name, string Email, string? Whatsapp, DateTime CreatedAt,
        string? RegistrationIp, string? RegistrationUserAgent, string? RegistrationAcceptLanguage,
        string? RegistrationCountryCode, string? RegistrationReferrerHost, string? RegistrationSource,
        Guid? RequestId, string Status, DateTime? FirstRequestedAt, DateTime? LastRequestedAt,
        DateTime? ReleasedAt, DateTime? UsedAt, DateTime? ClosedAt, int RequestCount, string? ReleasedBy);

    public sealed record FreeTrialAdminStatsDto(long NeverRequested, long NotUsed, long Requested, long Released, long Used);
    public sealed record FreeTrialAdminListDto(long Total, int Page, int Limit, FreeTrialAdminUserDto[] Users, FreeTrialAdminStatsDto Stats);
    public sealed record FreeTrialAdminTransitionResult(bool Success, string Message, FreeTrialStatusDto? Status);
}
