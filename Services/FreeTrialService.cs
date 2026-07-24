using Dapper;
using Npgsql;

namespace PremierAPI.Services
{
    public sealed class FreeTrialService
    {
        private static readonly HashSet<string> ReopenableStatuses = new(StringComparer.Ordinal)
        {
            "cancelado"
        };

        private readonly string _connectionString;
        private readonly ILogger<FreeTrialService> _logger;

        public FreeTrialService(
            IConfiguration configuration,
            ILogger<FreeTrialService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
            _logger = logger;
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
                    COALESCE(f.request_count, 0) AS ""RequestCount"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = u.id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
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

        public async Task<FreeTrialRequestResult?> RequestAsync(
            string? sessionToken,
            Guid? metaAttributionId = null)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return null;

            await using var db = new NpgsqlConnection(_connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();

            var identity = await db.QueryFirstOrDefaultAsync<FreeTrialIdentityRow>(@"
                SELECT
                    u.id AS ""UserId"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = u.id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
                FROM user_sessions s
                INNER JOIN users u ON u.id = s.user_id
                WHERE s.token = @Token
                  AND s.expires_at > @Now
                  AND u.is_active = true
                LIMIT 1
                FOR UPDATE OF u;",
                new { Token = sessionToken, Now = DateTime.UtcNow }, transaction);

            if (identity == null)
            {
                await transaction.RollbackAsync();
                return null;
            }

            if (identity.HasPaidOrder)
            {
                var ineligible = await GetUserStatusRowAsync(db, transaction, identity.UserId);
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(ineligible!), false, false, true);
            }

            var current = await db.QueryFirstOrDefaultAsync<FreeTrialUserRow>(@"
                SELECT
                    user_id AS ""UserId"",
                    id AS ""RequestId"",
                    status AS ""Status"",
                    first_requested_at AS ""FirstRequestedAt"",
                    last_requested_at AS ""LastRequestedAt"",
                    released_at AS ""ReleasedAt"",
                    used_at AS ""UsedAt"",
                    closed_at AS ""ClosedAt"",
                    f.request_count AS ""RequestCount"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = f.user_id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
                FROM free_trial_requests f
                WHERE f.user_id = @UserId
                FOR UPDATE OF f;",
                new { UserId = identity.UserId }, transaction);

            Guid? insertedId = null;
            if (current == null)
            {
                Guid? snapshotId = await TryCreateMetaAttributionSnapshotAsync(
                    db,
                    transaction,
                    metaAttributionId,
                    identity.UserId);
                insertedId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                    INSERT INTO free_trial_requests (user_id, meta_attribution_id)
                    VALUES (@UserId, @MetaAttributionId)
                    ON CONFLICT (user_id) DO NOTHING
                    RETURNING id;",
                    new { UserId = identity.UserId, MetaAttributionId = snapshotId },
                    transaction);
            }

            if (insertedId.HasValue)
            {
                await InsertEventAsync(db, transaction, insertedId.Value, "solicitado", "usuario", identity.UserId.ToString());
                var created = await GetUserStatusRowAsync(db, transaction, identity.UserId);
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(created!), true, false, false);
            }

            current ??= await GetUserStatusRowAsync(db, transaction, identity.UserId);
            if (current?.RequestId == null)
            {
                _logger.LogError(
                    "[TESTE GRATIS] A solicitação do usuário {UserId} não foi criada nem localizada após o conflito.",
                    identity.UserId);
                await transaction.RollbackAsync();
                throw new InvalidOperationException("Não foi possível registrar a solicitação de teste grátis.");
            }

            if (current.UsedAt.HasValue || string.Equals(current.Status, "utilizado", StringComparison.Ordinal))
            {
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(current), false, true, false);
            }

            if (!ReopenableStatuses.Contains(current.Status ?? ""))
            {
                await transaction.CommitAsync();
                return new FreeTrialRequestResult(ToStatus(current), false, false, false);
            }

            Guid? reopenedSnapshotId = await TryCreateMetaAttributionSnapshotAsync(
                db,
                transaction,
                metaAttributionId,
                identity.UserId);
            await db.ExecuteAsync(@"
                UPDATE free_trial_requests
                SET status = 'solicitado',
                    last_requested_at = CURRENT_TIMESTAMP,
                    request_count = request_count + 1,
                    released_at = NULL,
                    released_by = NULL,
                    closed_at = NULL,
                    meta_attribution_id = COALESCE(@MetaAttributionId, meta_attribution_id),
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @Id;",
                new
                {
                    Id = current.RequestId,
                    MetaAttributionId = reopenedSnapshotId
                },
                transaction);
            await InsertEventAsync(db, transaction, current.RequestId!.Value, "solicitado", "usuario", identity.UserId.ToString());

            var reopened = await GetUserStatusRowAsync(db, transaction, identity.UserId);
            await transaction.CommitAsync();
            return new FreeTrialRequestResult(ToStatus(reopened!), false, false, false);
        }

        public async Task<FreeTrialAdminListDto> GetAdminListAsync(
            string filter, int page, int limit, string search, string sortBy, string sortDir)
        {
            page = Math.Max(1, page);
            limit = Math.Clamp(limit, 1, 100);
            filter = (filter ?? "all").Trim().ToLowerInvariant();
            search = (search ?? "").Trim();
            const string paidOrderSql = @"EXISTS (
                SELECT 1 FROM orders paid_order
                WHERE paid_order.user_id = u.id
                  AND (paid_order.status = 'pago' OR COALESCE(paid_order.canceled_was_paid, false) = true)
            )";

            string filterSql = filter switch
            {
                "never_requested" => $"f.id IS NULL AND NOT ({paidOrderSql})",
                "not_used" => $"f.used_at IS NULL AND NOT ({paidOrderSql})",
                "solicitado" => $"f.status = 'solicitado' AND NOT ({paidOrderSql})",
                "liberado" => $"f.status = 'liberado' AND NOT ({paidOrderSql})",
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
                    {paidOrderSql} AS ""HasPaidOrder"",
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

            var stats = await db.QuerySingleAsync<FreeTrialAdminStatsDto>($@"
                SELECT
                    COUNT(*) FILTER (WHERE f.id IS NULL AND NOT ({paidOrderSql})) AS ""NeverRequested"",
                    COUNT(*) FILTER (WHERE f.used_at IS NULL AND NOT ({paidOrderSql})) AS ""NotUsed"",
                    COUNT(*) FILTER (WHERE f.status = 'solicitado' AND NOT ({paidOrderSql})) AS ""Requested"",
                    COUNT(*) FILTER (WHERE f.status = 'liberado' AND NOT ({paidOrderSql})) AS ""Released"",
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
                    f.request_count AS ""RequestCount"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = f.user_id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
                FROM free_trial_requests f
                WHERE f.id = @Id
                FOR UPDATE OF f;",
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
            if (action == "release" && current.HasPaidOrder)
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminTransitionResult(
                    false,
                    "Cliente com pedido pago não é elegível para teste grátis.",
                    ToStatus(current));
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
                new
                {
                    Status = targetStatus,
                    Actor = actor,
                    Id = requestId
                },
                transaction);
            await InsertEventAsync(db, transaction, requestId, targetStatus, "admin", actor);

            var updated = await GetUserStatusRowAsync(db, transaction, current.UserId);
            await transaction.CommitAsync();
            return new FreeTrialAdminTransitionResult(true, "Situação atualizada.", ToStatus(updated!));
        }

        public async Task<FreeTrialAdminTransitionResult?> ReleaseManuallyAsync(Guid userId, string actor)
        {
            await using var db = new NpgsqlConnection(_connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();

            var identity = await db.QueryFirstOrDefaultAsync<FreeTrialManualIdentityRow>(@"
                SELECT
                    u.id AS ""UserId"",
                    f.id AS ""RequestId"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = u.id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
                FROM users u
                LEFT JOIN free_trial_requests f ON f.user_id = u.id
                WHERE u.id = @UserId
                FOR UPDATE OF u;",
                new { UserId = userId }, transaction);

            if (identity == null)
            {
                await transaction.RollbackAsync();
                return null;
            }
            if (identity.RequestId.HasValue)
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminTransitionResult(false, "Este usuário já possui uma solicitação de teste grátis.", null);
            }
            if (identity.HasPaidOrder)
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminTransitionResult(false, "Cliente com pedido pago não é elegível para teste grátis.", null);
            }

            Guid? requestId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                INSERT INTO free_trial_requests
                    (user_id, status, released_at, released_by)
                VALUES
                    (@UserId, 'liberado', CURRENT_TIMESTAMP, @Actor)
                ON CONFLICT (user_id) DO NOTHING
                RETURNING id;",
                new { UserId = userId, Actor = actor }, transaction);

            if (!requestId.HasValue)
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminTransitionResult(false, "Este usuário já possui uma solicitação de teste grátis.", null);
            }

            await InsertEventAsync(db, transaction, requestId.Value, "liberado", "admin", actor);
            var created = await GetUserStatusRowAsync(db, transaction, userId);
            await transaction.CommitAsync();
            return new FreeTrialAdminTransitionResult(true, "Teste grátis liberado manualmente.", ToStatus(created!));
        }

        public async Task<FreeTrialAdminDeleteResult?> DeleteResettableAsync(Guid requestId)
        {
            await using var db = new NpgsqlConnection(_connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();

            string? status = await db.QueryFirstOrDefaultAsync<string>(@"
                SELECT status
                FROM free_trial_requests
                WHERE id = @Id
                FOR UPDATE;",
                new { Id = requestId }, transaction);

            if (status == null)
            {
                await transaction.RollbackAsync();
                return null;
            }
            if (status is not ("recusado" or "utilizado"))
            {
                await transaction.RollbackAsync();
                return new FreeTrialAdminDeleteResult(false, "Somente solicitações recusadas ou utilizadas podem ser excluídas.");
            }

            await db.ExecuteAsync(@"
                DELETE FROM free_trial_requests
                WHERE id = @Id;",
                new { Id = requestId }, transaction);
            await transaction.CommitAsync();
            return new FreeTrialAdminDeleteResult(true, "Solicitação excluída. O usuário voltou para nunca solicitou.");
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

        private async Task<Guid?> TryCreateMetaAttributionSnapshotAsync(
            NpgsqlConnection db,
            NpgsqlTransaction transaction,
            Guid? attributionId,
            Guid userId)
        {
            if (!attributionId.HasValue) return null;

            const string savepoint = "meta_attribution_snapshot";
            await db.ExecuteAsync($"SAVEPOINT {savepoint}", transaction: transaction);
            try
            {
                Guid? snapshotId = await db.QueryFirstOrDefaultAsync<Guid?>(@"
                    INSERT INTO meta_attributions
                        (id, user_id, source_attribution_id,
                         consent_status, consent_version, consented_at,
                         fbp, fbc, fbclid, client_ip_address, client_user_agent,
                         source_url, captured_at, updated_at)
                    SELECT
                        gen_random_uuid(), @UserId, COALESCE(source_attribution_id, id),
                        consent_status, consent_version,
                        consented_at, fbp, fbc, fbclid, client_ip_address,
                        client_user_agent, source_url, CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP
                    FROM meta_attributions
                    WHERE id = @AttributionId
                      AND consent_status = 'accepted'
                    RETURNING id;",
                    new { UserId = userId, AttributionId = attributionId.Value },
                    transaction);
                await db.ExecuteAsync($"RELEASE SAVEPOINT {savepoint}", transaction: transaction);
                return snapshotId;
            }
            catch (Exception ex)
            {
                await db.ExecuteAsync($"ROLLBACK TO SAVEPOINT {savepoint}", transaction: transaction);
                await db.ExecuteAsync($"RELEASE SAVEPOINT {savepoint}", transaction: transaction);
                _logger.LogError(
                    ex,
                    "[META ATRIBUICAO] Falha ao criar snapshot para teste grátis do usuário {UserId}; solicitação continuará sem marketing.",
                    userId);
                return null;
            }
        }

        private static Task<FreeTrialUserRow?> GetUserStatusRowAsync(
            NpgsqlConnection db, NpgsqlTransaction transaction, Guid userId)
        {
            return db.QueryFirstOrDefaultAsync<FreeTrialUserRow>(@"
                SELECT
                    u.id AS ""UserId"", f.id AS ""RequestId"", f.status AS ""Status"",
                    f.first_requested_at AS ""FirstRequestedAt"", f.last_requested_at AS ""LastRequestedAt"",
                    f.released_at AS ""ReleasedAt"", f.used_at AS ""UsedAt"", f.closed_at AS ""ClosedAt"",
                    COALESCE(f.request_count, 0) AS ""RequestCount"",
                    EXISTS (
                        SELECT 1 FROM orders o
                        WHERE o.user_id = u.id
                          AND (o.status = 'pago' OR COALESCE(o.canceled_was_paid, false) = true)
                    ) AS ""HasPaidOrder""
                FROM users u
                LEFT JOIN free_trial_requests f ON f.user_id = u.id
                WHERE u.id = @UserId;",
                new { UserId = userId }, transaction);
        }

        private static FreeTrialStatusDto ToStatus(FreeTrialUserRow row)
        {
            string status = string.IsNullOrWhiteSpace(row.Status) ? "nao_solicitado" : row.Status;
            bool hasUsed = row.UsedAt.HasValue || status == "utilizado";
            bool eligible = !row.HasPaidOrder;
            bool canRequest = eligible && (status is "nao_solicitado" or "cancelado") && !hasUsed;
            return new FreeTrialStatusDto(
                row.RequestId, status, eligible, row.HasPaidOrder, canRequest, hasUsed,
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
            public bool HasPaidOrder { get; set; }
        }

        private sealed class FreeTrialIdentityRow
        {
            public Guid UserId { get; set; }
            public bool HasPaidOrder { get; set; }
        }

        private sealed class FreeTrialManualIdentityRow
        {
            public Guid UserId { get; set; }
            public Guid? RequestId { get; set; }
            public bool HasPaidOrder { get; set; }
        }
    }

    public sealed record FreeTrialStatusDto(
        Guid? RequestId, string Status, bool Eligible, bool HasPaidOrder, bool CanRequest, bool HasUsed,
        DateTime? FirstRequestedAt, DateTime? LastRequestedAt, DateTime? ReleasedAt,
        DateTime? UsedAt, DateTime? ClosedAt, int RequestCount);

    public sealed record FreeTrialRequestResult(
        FreeTrialStatusDto Status, bool Created, bool AlreadyUsed, bool IneligibleDueToPaidOrder);

    public sealed record FreeTrialAdminUserDto(
        Guid UserId, string Name, string Email, string? Whatsapp, DateTime CreatedAt,
        string? RegistrationIp, string? RegistrationUserAgent, string? RegistrationAcceptLanguage,
        string? RegistrationCountryCode, string? RegistrationReferrerHost, string? RegistrationSource,
        bool HasPaidOrder,
        Guid? RequestId, string Status, DateTime? FirstRequestedAt, DateTime? LastRequestedAt,
        DateTime? ReleasedAt, DateTime? UsedAt, DateTime? ClosedAt, int RequestCount, string? ReleasedBy);

    public sealed record FreeTrialAdminStatsDto(long NeverRequested, long NotUsed, long Requested, long Released, long Used);
    public sealed record FreeTrialAdminListDto(long Total, int Page, int Limit, FreeTrialAdminUserDto[] Users, FreeTrialAdminStatsDto Stats);
    public sealed record FreeTrialAdminTransitionResult(bool Success, string Message, FreeTrialStatusDto? Status);
    public sealed record FreeTrialAdminDeleteResult(bool Success, string Message);
}
