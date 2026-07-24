using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class ClientAuthPostgresTests
{
    [Fact]
    public async Task LegacyMigrationBackfillsHashesClearsPlaintextAndPreservesLinks()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateLegacySchemaAsync(db);
            Guid userId = Guid.NewGuid();
            string legacySession = new('a', 64);
            string legacyConfirmation = Guid.Empty.ToString("D");
            string intermediateConfirmation = new SecurityTokenService()
                .HashToken("intermediate-confirmation-link");
            string legacyReset = new('b', 32);

            await db.ExecuteAsync(@"
                INSERT INTO users
                    (id, name, email, password_hash, is_active, email_confirmed,
                     email_confirmation_token, email_confirmation_token_hash,
                     password_reset_token, password_reset_expires)
                VALUES
                    (@UserId, 'Legacy User', 'legacy@example.test', 'hash',
                     true, false, @Confirmation, @IntermediateConfirmation,
                     @Reset,
                     CURRENT_TIMESTAMP + INTERVAL '1 hour');
                INSERT INTO user_sessions (user_id, token, expires_at)
                VALUES (@UserId, @Session, CURRENT_TIMESTAMP + INTERVAL '7 days');",
                new
                {
                    UserId = userId,
                    Confirmation = legacyConfirmation,
                    IntermediateConfirmation = intermediateConfirmation,
                    Reset = legacyReset,
                    Session = legacySession
                });

            DatabaseInitializer.MigrateClientAuthTokens(db);
            DatabaseInitializer.MigrateClientAuthTokens(db);

            var state = await db.QuerySingleAsync<MigrationState>(@"
                SELECT
                    s.token IS NULL AS ""SessionPlaintextCleared"",
                    s.token_hash ~ '^[0-9a-f]{64}$' AS ""SessionHashValid"",
                    u.email_confirmation_token IS NULL AS ""EmailPlaintextCleared"",
                    u.email_confirmation_token_hash IS NULL AS ""EmailHashCleared"",
                    u.password_reset_token IS NULL AS ""ResetPlaintextCleared"",
                    u.password_reset_token_hash ~ '^[0-9a-f]{64}$' AS ""ResetHashValid""
                FROM users u
                JOIN user_sessions s ON s.user_id = u.id
                WHERE u.id = @UserId;",
                new { UserId = userId });
            Assert.True(
                state.SessionPlaintextCleared &&
                state.SessionHashValid &&
                state.EmailPlaintextCleared &&
                state.EmailHashCleared &&
                state.ResetPlaintextCleared &&
                state.ResetHashValid);

            var sessions = SessionService(connectionString);
            Assert.True((await sessions.FindUserIdAsync(legacySession)).HasValue);
            var tokens = new SecurityTokenService();
            Assert.Equal(2, await db.QuerySingleAsync<int>(@"
                SELECT COUNT(*)
                FROM email_confirmation_tokens
                WHERE user_id = @UserId
                  AND token_hash IN (@RawHash, @IntermediateHash);",
                new
                {
                    UserId = userId,
                    RawHash = tokens.HashToken(legacyConfirmation),
                    IntermediateHash = intermediateConfirmation
                }));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT password_reset_token_hash = @ResetHash
                FROM users
                WHERE id = @UserId;",
                new
                {
                    UserId = userId,
                    ResetHash = tokens.HashToken(legacyReset)
                }));

            var confirmationTokens =
                ConfirmationTokenService(connectionString);
            EmailConfirmationResult confirmed =
                await confirmationTokens.ConfirmByTokenAsync(
                    legacyConfirmation);
            Assert.Equal(
                EmailConfirmationResultStatus.Confirmed,
                confirmed.Status);
            Assert.Equal(
                EmailConfirmationResultStatus.InvalidOrUsed,
                (await confirmationTokens.ConfirmByTokenAsync(
                    legacyConfirmation)).Status);

            ClientAuthStorageStatus status =
                DatabaseInitializer.GetClientAuthStorageStatus(
                    Configuration(connectionString));
            Assert.True(
                status.SessionsWithoutHash == 0 &&
                status.LegacyTokensNotNull == 0 &&
                status.InvalidHashes == 0);
        });
    }

    [Fact]
    public async Task SessionsSupportAuthenticationLogoutRotationRevocationAndRollback()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO users (id, password_hash, is_active)
                VALUES (@UserId, 'old-password-hash', true);",
                new { UserId = userId });
            ClientSessionService sessions = SessionService(connectionString);

            ClientSessionIssue first;
            ClientSessionIssue second;
            await using (var transaction = await db.BeginTransactionAsync())
            {
                first = await sessions.CreateSessionAsync(db, transaction, userId);
                second = await sessions.CreateSessionAsync(db, transaction, userId);
                await transaction.CommitAsync();
            }

            Assert.True(first.Token != second.Token);
            var stored = (await db.QueryAsync<StoredSession>(@"
                SELECT token AS ""PlainToken"", token_hash AS ""TokenHash""
                FROM user_sessions
                WHERE user_id = @UserId;",
                new { UserId = userId })).ToArray();
            Assert.True(stored.Length == 2);
            Assert.All(stored, row => Assert.True(
                row.PlainToken == null &&
                SecurityTokenService.IsValidHash(row.TokenHash)));
            Assert.True((await sessions.FindUserIdAsync(first.Token)).HasValue);
            Assert.True((await sessions.FindUserIdAsync(second.Token)).HasValue);
            Assert.False((await sessions.FindUserIdAsync("invalid")).HasValue);

            await db.ExecuteAsync(
                "UPDATE user_sessions SET expires_at = CURRENT_TIMESTAMP - INTERVAL '1 minute' WHERE token_hash = @Hash;",
                new
                {
                    Hash = new SecurityTokenService().HashToken(first.Token)
                });
            Assert.False((await sessions.FindUserIdAsync(first.Token)).HasValue);

            await db.ExecuteAsync(
                "UPDATE users SET is_active = false WHERE id = @UserId;",
                new { UserId = userId });
            Assert.False((await sessions.FindUserIdAsync(first.Token)).HasValue);
            await db.ExecuteAsync(
                "UPDATE users SET is_active = true WHERE id = @UserId;",
                new { UserId = userId });

            Guid? loggedOut = await sessions.LogoutAsync(
                first.Token,
                new ClientSessionActivity(null, null, null, null, null));
            Assert.True(loggedOut.HasValue);
            Assert.False((await sessions.FindUserIdAsync(first.Token)).HasValue);
            Assert.False((await sessions.LogoutAsync(
                first.Token,
                new ClientSessionActivity(null, null, null, null, null))).HasValue);

            ClientSessionIssue rotated;
            await using (var transaction = await db.BeginTransactionAsync())
            {
                rotated = await sessions.RotateSessionAsync(
                    db,
                    transaction,
                    userId);
                await transaction.CommitAsync();
            }
            Assert.False((await sessions.FindUserIdAsync(second.Token)).HasValue);
            Assert.True((await sessions.FindUserIdAsync(rotated.Token)).HasValue);

            ClientSessionIssue rolledBack;
            await using (var transaction = await db.BeginTransactionAsync())
            {
                await db.ExecuteAsync(
                    "UPDATE users SET password_hash = 'new-password-hash' WHERE id = @UserId;",
                    new { UserId = userId },
                    transaction);
                rolledBack = await sessions.RotateSessionAsync(
                    db,
                    transaction,
                    userId);
                await transaction.RollbackAsync();
            }
            Assert.True((await sessions.FindUserIdAsync(rotated.Token)).HasValue);
            Assert.False((await sessions.FindUserIdAsync(rolledBack.Token)).HasValue);
            Assert.True(await db.QuerySingleAsync<bool>(
                "SELECT password_hash = 'old-password-hash' FROM users WHERE id = @UserId;",
                new { UserId = userId }));

            await using (var transaction = await db.BeginTransactionAsync())
            {
                await sessions.RevokeAllSessionsAsync(db, transaction, userId);
                await transaction.CommitAsync();
            }
            Assert.False((await sessions.FindUserIdAsync(rotated.Token)).HasValue);
            Assert.True(await db.QuerySingleAsync<bool>(
                "SELECT NOT EXISTS (SELECT 1 FROM user_sessions WHERE user_id = @UserId);",
                new { UserId = userId }));
        });
    }

    [Fact]
    public async Task MultipleConfirmationTokensAreHashedConcurrentAndSingleUse()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO users
                    (id, name, email, password_hash, is_active, email_confirmed)
                VALUES
                    (@UserId, 'Synthetic User', 'synthetic@example.test',
                     'old', true, false);",
                new { UserId = userId });

            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            EmailConfirmationTokenIssue first;
            EmailConfirmationTokenIssue second;
            await using (var transaction = await db.BeginTransactionAsync())
            {
                first = await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    userId,
                    "Synthetic User",
                    "synthetic@example.test");
                second = await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    userId,
                    "Synthetic User",
                    "synthetic@example.test");
                await transaction.CommitAsync();
            }

            Assert.Equal(2, await db.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM email_confirmation_tokens WHERE user_id = @UserId;",
                new { UserId = userId }));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT email_confirmation_token IS NULL
                FROM users
                WHERE id = @UserId;",
                new { UserId = userId }));
            Assert.False(await db.QuerySingleAsync<bool>(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM email_confirmation_tokens
                    WHERE token_hash = @RawFirst
                       OR token_hash = @RawSecond
                );",
                new { RawFirst = first.Token, RawSecond = second.Token }));

            EmailConfirmationResult[] results = await Task.WhenAll(
                confirmations.ConfirmByTokenAsync(first.Token),
                confirmations.ConfirmByTokenAsync(second.Token));
            Assert.Single(results, result =>
                result.Status == EmailConfirmationResultStatus.Confirmed);
            Assert.Single(results, result =>
                result.Status == EmailConfirmationResultStatus.InvalidOrUsed);
            Assert.Equal(1, results.Count(result =>
                result.Status == EmailConfirmationResultStatus.Confirmed));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT bool_and(used_at IS NOT NULL)
                FROM email_confirmation_tokens
                WHERE user_id = @UserId;",
                new { UserId = userId }));
            Assert.Equal(
                EmailConfirmationResultStatus.InvalidOrUsed,
                (await confirmations.ConfirmByTokenAsync(first.Token)).Status);

            var duplicate = await Assert.ThrowsAsync<PostgresException>(
                async () => await db.ExecuteAsync(@"
                    INSERT INTO email_confirmation_tokens
                        (user_id, token_hash, expires_at, delivery_kind)
                    VALUES
                        (@UserId, @TokenHash, 'infinity'::timestamp, 'migrated');",
                    new
                    {
                        UserId = userId,
                        TokenHash = new SecurityTokenService().HashToken(
                            first.Token)
                    }));
            Assert.Equal(
                PostgresErrorCodes.UniqueViolation,
                duplicate.SqlState);

            Guid adminUserId = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO users
                    (id, name, email, password_hash, is_active, email_confirmed)
                VALUES
                    (@UserId, 'Admin Confirmed', 'admin-confirmed@example.test',
                     'old', true, false);",
                new { UserId = adminUserId });
            await using (var transaction = await db.BeginTransactionAsync())
            {
                await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    adminUserId,
                    "Admin Confirmed",
                    "admin-confirmed@example.test");
                await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    adminUserId,
                    "Admin Confirmed",
                    "admin-confirmed@example.test");
                await transaction.CommitAsync();
            }
            Assert.Equal(
                EmailConfirmationResultStatus.Confirmed,
                (await confirmations.ConfirmUserAsync(adminUserId)).Status);
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT bool_and(used_at IS NOT NULL)
                FROM email_confirmation_tokens
                WHERE user_id = @UserId;",
                new { UserId = adminUserId }));
        });
    }

    [Fact]
    public async Task RecoveryHashesAreSingleUseReplaceableAndExpiring()
    {
        await WithIsolatedSchemaAsync(async (_, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            var tokens = new SecurityTokenService();
            Guid userId = Guid.NewGuid();
            string reset = tokens.GenerateToken();
            await db.ExecuteAsync(@"
                INSERT INTO users
                    (id, name, email, password_hash, is_active,
                     password_reset_token_hash, password_reset_expires)
                VALUES
                    (@UserId, 'Reset User', 'reset@example.test',
                     'old', true, @ResetHash,
                     CURRENT_TIMESTAMP + INTERVAL '1 hour');",
                new
                {
                    UserId = userId,
                    ResetHash = tokens.HashToken(reset)
                });

            int consumed = await db.ExecuteAsync(@"
                UPDATE users
                SET password_hash = 'new',
                    password_reset_token_hash = NULL,
                    password_reset_expires = NULL
                WHERE id = @UserId
                  AND password_reset_token_hash = @ResetHash
                  AND password_reset_expires > CURRENT_TIMESTAMP;",
                new { UserId = userId, ResetHash = tokens.HashToken(reset) });
            int resetReplay = await db.ExecuteAsync(@"
                UPDATE users
                SET password_hash = 'newer'
                WHERE id = @UserId
                  AND password_reset_token_hash = @ResetHash
                  AND password_reset_expires > CURRENT_TIMESTAMP;",
                new { UserId = userId, ResetHash = tokens.HashToken(reset) });
            Assert.True(consumed == 1 && resetReplay == 0);

            string supersededReset = tokens.GenerateToken();
            string currentReset = tokens.GenerateToken();
            await db.ExecuteAsync(@"
                UPDATE users
                SET password_reset_token_hash = @ResetHash,
                    password_reset_expires =
                        CURRENT_TIMESTAMP + INTERVAL '1 hour'
                WHERE id = @UserId;",
                new
                {
                    UserId = userId,
                    ResetHash = tokens.HashToken(supersededReset)
                });
            await db.ExecuteAsync(@"
                UPDATE users
                SET password_reset_token_hash = @ResetHash
                WHERE id = @UserId;",
                new
                {
                    UserId = userId,
                    ResetHash = tokens.HashToken(currentReset)
                });
            Assert.False(await HasResetHashAsync(
                db,
                userId,
                tokens.HashToken(supersededReset)));
            Assert.True(await HasResetHashAsync(
                db,
                userId,
                tokens.HashToken(currentReset)));

            await db.ExecuteAsync(@"
                UPDATE users
                SET password_reset_expires =
                    CURRENT_TIMESTAMP - INTERVAL '1 minute'
                WHERE id = @UserId;",
                new { UserId = userId });
            Assert.False(await db.QuerySingleAsync<bool>(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM users
                    WHERE id = @UserId
                      AND password_reset_token_hash = @ResetHash
                      AND password_reset_expires > CURRENT_TIMESTAMP
                );",
                new
                {
                    UserId = userId,
                    ResetHash = tokens.HashToken(currentReset)
                }));
        });
    }

    [Fact]
    public async Task ConfirmationCleanupRemovesOnlyOldUsedOrExpiredTokens()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO users
                    (id, name, email, password_hash, is_active, email_confirmed)
                VALUES
                    (@UserId, 'Cleanup User', 'cleanup@example.test',
                     'hash', true, false);",
                new { UserId = userId });
            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            EmailConfirmationTokenIssue valid;
            await using (var transaction = await db.BeginTransactionAsync())
            {
                valid = await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    userId,
                    "Cleanup User",
                    "cleanup@example.test");
                await transaction.CommitAsync();
            }

            var tokens = new SecurityTokenService();
            await db.ExecuteAsync(@"
                INSERT INTO email_confirmation_tokens
                    (user_id, token_hash, expires_at, used_at, delivery_kind)
                VALUES
                    (@UserId, @UsedHash, 'infinity'::timestamp,
                     CURRENT_TIMESTAMP - INTERVAL '8 days', 'migrated'),
                    (@UserId, @ExpiredHash,
                     CURRENT_TIMESTAMP - INTERVAL '8 days',
                     NULL, 'migrated');",
                new
                {
                    UserId = userId,
                    UsedHash = tokens.HashToken(tokens.GenerateToken()),
                    ExpiredHash = tokens.HashToken(tokens.GenerateToken())
                });

            EmailConfirmationCleanupResult cleanup =
                await confirmations.CleanupAsync();
            Assert.Equal(2, cleanup.DeletedOldTokens);
            Assert.True(await HasEmailHashAsync(
                db,
                userId,
                tokens.HashToken(valid.Token)));
            Assert.Equal(1, await db.QuerySingleAsync<int>(@"
                SELECT COUNT(*)
                FROM email_confirmation_tokens
                WHERE user_id = @UserId;",
                new { UserId = userId }));
            ClientAuthStorageStatus status =
                DatabaseInitializer.GetClientAuthStorageStatus(
                    Configuration(connectionString));
            Assert.Equal(0, status.LegacyTokensNotNull);
            Assert.Equal(0, status.InvalidHashes);
        });
    }

    [Fact]
    public async Task InitialConfirmationCommitsBeforeSmtpAndFailureKeepsUserAndHash()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            const string email = "initial@example.test";
            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            EmailConfirmationTokenIssue issue;

            await using (var transaction = await db.BeginTransactionAsync())
            {
                await db.ExecuteAsync(@"
                    INSERT INTO users
                        (id, name, email, password_hash, is_active,
                         email_confirmed)
                    VALUES
                        (@UserId, 'Initial User', @Email, 'hash', true, false);",
                    new { UserId = userId, Email = email },
                    transaction);
                issue = await confirmations.CreateInitialTokenAsync(
                    db,
                    transaction,
                    userId,
                    "Initial User",
                    email);
                await transaction.CommitAsync();
            }

            var sender = new SyntheticConfirmationSender(
                failure: new TimeoutException(
                    "Synthetic ambiguous timeout."),
                onSend: async () =>
                {
                    await using var independent =
                        new NpgsqlConnection(connectionString);
                    Assert.True(await independent.QuerySingleAsync<bool>(@"
                        SELECT EXISTS (
                            SELECT 1
                            FROM users u
                            INNER JOIN email_confirmation_tokens t
                                ON t.user_id = u.id
                            WHERE u.id = @UserId
                              AND t.token_hash = @TokenHash
                        );",
                        new
                        {
                            UserId = userId,
                            TokenHash =
                                new SecurityTokenService().HashToken(issue.Token)
                        }));
                });
            Exception failure = await Assert.ThrowsAsync<TimeoutException>(
                async () => await sender.SendAsync(
                    issue.Email,
                    issue.Name,
                    issue.Token));
            await confirmations.MarkFailedAsync(
                issue.TokenId,
                EmailConfirmationFailureSanitizer.Code(failure),
                reminderRetryAt: null);

            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT EXISTS (SELECT 1 FROM users WHERE id = @UserId)
                   AND EXISTS (
                       SELECT 1
                       FROM email_confirmation_tokens
                       WHERE id = @TokenId
                         AND used_at IS NULL
                         AND failed_at IS NOT NULL
                   );",
                new { UserId = userId, issue.TokenId }));
            Assert.Equal(
                EmailConfirmationResultStatus.Confirmed,
                (await confirmations.ConfirmByTokenAsync(
                    issue.Token)).Status);
        });
    }

    [Fact]
    public async Task ReminderTimeoutKeepsPotentiallyDeliveredAndPreviousLinksValid()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            EmailConfirmationTokenIssue previous =
                await SeedDueConfirmationUserAsync(
                    db,
                    confirmations,
                    userId);
            var failingSender = new SyntheticConfirmationSender(
                new TimeoutException("Synthetic ambiguous timeout."));
            var failingWorker = new EmailConfirmationReminderWorker(
                WorkerConfiguration(connectionString),
                failingSender,
                NullLogger<EmailConfirmationReminderWorker>.Instance,
                confirmations);

            await failingWorker.ProcessDueRemindersAsync(CancellationToken.None);

            Assert.True(await HasEmailHashAsync(
                db,
                userId,
                new SecurityTokenService().HashToken(previous.Token)));
            Assert.True(await HasEmailHashAsync(
                db,
                userId,
                failingSender.ProvidedTokenHash));
            Assert.Equal(2, await db.QuerySingleAsync<int>(@"
                SELECT COUNT(*)
                FROM email_confirmation_tokens
                WHERE user_id = @UserId
                  AND used_at IS NULL;",
                new { UserId = userId }));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT email_confirmation_resend_count = 0
                   AND email_confirmation_next_send_at > CURRENT_TIMESTAMP
                FROM users
                WHERE id = @UserId;",
                new { UserId = userId }));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT failed_at IS NOT NULL
                   AND sanitized_failure_code = 'smtp_timeout'
                FROM email_confirmation_tokens
                WHERE token_hash = @TokenHash;",
                new { TokenHash = failingSender.ProvidedTokenHash }));
            Assert.Equal(
                EmailConfirmationResultStatus.Confirmed,
                (await confirmations.ConfirmByTokenAsync(
                    failingSender.ProvidedToken!)).Status);
        });
    }

    [Fact]
    public async Task ReminderCommitsBeforeSmtpKeepsPreviousLinkAndCountsOnlySuccess()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            EmailConfirmationTokenIssue previous =
                await SeedDueConfirmationUserAsync(
                    db,
                    confirmations,
                    userId);

            var successfulSender = new SyntheticConfirmationSender(
                onSend: async () =>
                {
                    await using var independent =
                        new NpgsqlConnection(connectionString);
                    await independent.OpenAsync();
                    await independent.ExecuteAsync(
                        "SET lock_timeout = '250ms';");
                    await independent.ExecuteAsync(
                        "UPDATE users SET name = name WHERE id = @UserId;",
                        new { UserId = userId });
                });
            var worker = new EmailConfirmationReminderWorker(
                WorkerConfiguration(connectionString),
                successfulSender,
                NullLogger<EmailConfirmationReminderWorker>.Instance,
                confirmations);

            await worker.ProcessDueRemindersAsync(CancellationToken.None);

            Assert.True(await HasEmailHashAsync(
                db,
                userId,
                new SecurityTokenService().HashToken(previous.Token)));
            Assert.True(await HasEmailHashAsync(
                db,
                userId,
                successfulSender.ProvidedTokenHash));
            Assert.True(await db.QuerySingleAsync<bool>(@"
                SELECT email_confirmation_resend_count = 1
                   AND email_confirmation_last_sent_at IS NOT NULL
                FROM users
                WHERE id = @UserId;",
                new { UserId = userId }));
            Assert.Equal(
                EmailConfirmationResultStatus.Confirmed,
                (await confirmations.ConfirmByTokenAsync(
                    successfulSender.ProvidedToken!)).Status);
            Assert.Equal(
                EmailConfirmationResultStatus.InvalidOrUsed,
                (await confirmations.ConfirmByTokenAsync(
                    previous.Token)).Status);
        });
    }

    [Fact]
    public async Task ReminderClaimPreventsConcurrentSendAndAbandonedClaimRecovers()
    {
        await WithIsolatedSchemaAsync(async (connectionString, db) =>
        {
            await CreateHardenedSchemaAsync(db);
            Guid userId = Guid.NewGuid();
            EmailConfirmationTokenService confirmations =
                ConfirmationTokenService(connectionString);
            await SeedDueConfirmationUserAsync(
                db,
                confirmations,
                userId);

            EmailConfirmationTokenIssue? abandoned =
                await confirmations.PrepareDueReminderAsync(DateTime.UtcNow);
            Assert.NotNull(abandoned);
            Assert.Null(await confirmations.PrepareDueReminderAsync(
                DateTime.UtcNow));
            await db.ExecuteAsync(@"
                UPDATE email_confirmation_tokens
                SET claim_expires_at = CURRENT_TIMESTAMP - INTERVAL '1 minute'
                WHERE id = @TokenId;",
                new { TokenId = abandoned!.TokenId });
            EmailConfirmationCleanupResult cleanup =
                await confirmations.CleanupAsync();
            Assert.Equal(1, cleanup.RecoveredClaims);
            EmailConfirmationTokenIssue? recovered =
                await confirmations.PrepareDueReminderAsync(DateTime.UtcNow);
            Assert.NotNull(recovered);
            await confirmations.MarkFailedAsync(
                recovered!.TokenId,
                "smtp_failure",
                DateTime.UtcNow.AddMinutes(30));
            await db.ExecuteAsync(@"
                UPDATE users
                SET email_confirmation_next_send_at =
                    CURRENT_TIMESTAMP - INTERVAL '1 minute'
                WHERE id = @UserId;",
                new { UserId = userId });

            var blockingSender = new SyntheticConfirmationSender(
                blockDelivery: true);
            var firstWorker = new EmailConfirmationReminderWorker(
                WorkerConfiguration(connectionString),
                blockingSender,
                NullLogger<EmailConfirmationReminderWorker>.Instance,
                confirmations);
            var secondWorker = new EmailConfirmationReminderWorker(
                WorkerConfiguration(connectionString),
                blockingSender,
                NullLogger<EmailConfirmationReminderWorker>.Instance,
                confirmations);

            Task firstRun = firstWorker.ProcessDueRemindersAsync(
                CancellationToken.None);
            await blockingSender.WaitUntilEnteredAsync();
            Task secondRun = secondWorker.ProcessDueRemindersAsync(
                CancellationToken.None);
            await secondRun;
            Assert.Equal(1, blockingSender.SendCount);
            blockingSender.ReleaseDelivery();
            await firstRun;

            Assert.Equal(1, await db.QuerySingleAsync<int>(@"
                SELECT email_confirmation_resend_count
                FROM users
                WHERE id = @UserId;",
                new { UserId = userId }));
        });
    }

    private static async Task<bool> HasEmailHashAsync(
        NpgsqlConnection db,
        Guid userId,
        string hash) =>
        await db.QuerySingleAsync<bool>(@"
            SELECT EXISTS (
                SELECT 1
                FROM email_confirmation_tokens
                WHERE user_id = @UserId
                  AND token_hash = @Hash
                  AND used_at IS NULL
                  AND expires_at > CURRENT_TIMESTAMP
            );",
            new { Hash = hash, UserId = userId });

    private static async Task<bool> HasResetHashAsync(
        NpgsqlConnection db,
        Guid userId,
        string hash) =>
        await db.QuerySingleAsync<bool>(@"
            SELECT password_reset_token_hash = @Hash
            FROM users
            WHERE id = @UserId;",
            new { Hash = hash, UserId = userId });

    private static ClientSessionService SessionService(string connectionString) =>
        new(
            Configuration(connectionString),
            new SecurityTokenService(),
            TimeProvider.System);

    private static EmailConfirmationTokenService ConfirmationTokenService(
        string connectionString) =>
        new(
            Configuration(connectionString),
            new SecurityTokenService(),
            TimeProvider.System);

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            })
            .Build();

    private static IConfiguration WorkerConfiguration(
        string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["EmailConfirmationReminders:TimeZone"] = "UTC"
            })
            .Build();

    private static async Task<EmailConfirmationTokenIssue>
        SeedDueConfirmationUserAsync(
            NpgsqlConnection db,
            EmailConfirmationTokenService confirmations,
            Guid userId)
    {
        string email = $"{userId:N}@example.test";
        await db.ExecuteAsync(@"
            INSERT INTO users
                (id, name, email, password_hash, is_active, email_confirmed,
                 email_confirmation_resend_count,
                 email_confirmation_next_send_at, created_at)
            VALUES
                (@UserId, 'Synthetic User', @Email, 'hash', true, false, 0,
                 CURRENT_TIMESTAMP - INTERVAL '1 minute',
                 CURRENT_TIMESTAMP - INTERVAL '1 day');",
            new { UserId = userId, Email = email });

        EmailConfirmationTokenIssue initial;
        await using (var transaction = await db.BeginTransactionAsync())
        {
            initial = await confirmations.CreateInitialTokenAsync(
                db,
                transaction,
                userId,
                "Synthetic User",
                email);
            await transaction.CommitAsync();
        }
        await confirmations.MarkSentAsync(
            initial.TokenId,
            DateTime.UtcNow.AddDays(-1));
        return initial;
    }

    private static async Task WithIsolatedSchemaAsync(
        Func<string, NpgsqlConnection, Task> test)
    {
        string? baseConnectionString =
            Environment.GetEnvironmentVariable("PREMIERAPI_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        if (string.IsNullOrWhiteSpace(builder.Database) ||
            !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PREMIERAPI_TEST_CONNECTION_STRING deve apontar para um banco cujo nome contenha 'test'.");
        }

        string schema = "client_auth_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {schema};");
        try
        {
            builder.SearchPath = schema;
            await using var db = new NpgsqlConnection(builder.ConnectionString);
            await db.OpenAsync();
            await test(builder.ConnectionString, db);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE;");
        }
    }

    private static Task CreateLegacySchemaAsync(NpgsqlConnection db) =>
        db.ExecuteAsync(@"
            CREATE TABLE users (
                id UUID PRIMARY KEY,
                name VARCHAR(100) NOT NULL,
                email VARCHAR(150) NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                is_active BOOLEAN NOT NULL,
                email_confirmed BOOLEAN NOT NULL DEFAULT false,
                email_confirmation_token VARCHAR(255),
                email_confirmation_token_hash CHAR(64),
                email_confirmation_resend_count INT NOT NULL DEFAULT 0,
                email_confirmation_next_send_at TIMESTAMP,
                password_reset_token VARCHAR(255),
                password_reset_expires TIMESTAMP,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE user_sessions (
                id BIGSERIAL PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                token VARCHAR(255) UNIQUE NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                expires_at TIMESTAMP NOT NULL
            );");

    private static Task CreateHardenedSchemaAsync(NpgsqlConnection db) =>
        db.ExecuteAsync(@"
            CREATE TABLE users (
                id UUID PRIMARY KEY,
                name VARCHAR(100),
                email VARCHAR(150),
                password_hash VARCHAR(255) NOT NULL,
                is_active BOOLEAN NOT NULL,
                email_confirmed BOOLEAN NOT NULL DEFAULT false,
                email_confirmation_token VARCHAR(255),
                email_confirmation_resend_count INT NOT NULL DEFAULT 0,
                email_confirmation_last_sent_at TIMESTAMP,
                email_confirmation_next_send_at TIMESTAMP,
                password_reset_token VARCHAR(255),
                password_reset_token_hash CHAR(64),
                password_reset_expires TIMESTAMP,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE email_confirmation_tokens (
                id BIGSERIAL PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                token_hash CHAR(64) NOT NULL UNIQUE,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                expires_at TIMESTAMP NOT NULL,
                sent_at TIMESTAMP,
                used_at TIMESTAMP,
                delivery_kind VARCHAR(20) NOT NULL,
                claim_expires_at TIMESTAMP,
                failed_at TIMESTAMP,
                sanitized_failure_code VARCHAR(40)
            );
            CREATE TABLE user_sessions (
                id BIGSERIAL PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                token VARCHAR(255),
                token_hash CHAR(64) UNIQUE NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                expires_at TIMESTAMP NOT NULL
            );
            CREATE TABLE user_activity_events (
                id BIGSERIAL PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                event_type VARCHAR(20) NOT NULL,
                ip_address INET,
                user_agent VARCHAR(512),
                accept_language VARCHAR(200),
                country_code VARCHAR(2),
                referrer_host VARCHAR(150),
                occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );");

    private sealed class MigrationState
    {
        public bool SessionPlaintextCleared { get; set; }
        public bool SessionHashValid { get; set; }
        public bool EmailPlaintextCleared { get; set; }
        public bool EmailHashCleared { get; set; }
        public bool ResetPlaintextCleared { get; set; }
        public bool ResetHashValid { get; set; }
    }

    private sealed class StoredSession
    {
        public string? PlainToken { get; set; }
        public string TokenHash { get; set; } = "";
    }

    private sealed class SyntheticConfirmationSender :
        IEmailConfirmationSender
    {
        private readonly Exception? _failure;
        private readonly Func<Task>? _onSend;
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockDelivery;
        private readonly SecurityTokenService _tokens = new();
        private int _sendCount;

        public SyntheticConfirmationSender(
            Exception? failure = null,
            Func<Task>? onSend = null,
            bool blockDelivery = false)
        {
            _failure = failure;
            _onSend = onSend;
            _blockDelivery = blockDelivery;
        }

        public int SendCount => Volatile.Read(ref _sendCount);
        public string? ProvidedToken { get; private set; }
        public string ProvidedTokenHash { get; private set; } = "";

        public async Task SendAsync(
            string email,
            string name,
            string token,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sendCount);
            ProvidedToken = token;
            ProvidedTokenHash = _tokens.HashToken(token);
            if (_onSend != null) await _onSend();
            _entered.TrySetResult();
            if (_blockDelivery)
                await _release.Task.WaitAsync(cancellationToken);
            if (_failure != null) throw _failure;
        }

        public Task WaitUntilEnteredAsync() => _entered.Task;

        public void ReleaseDelivery() => _release.TrySetResult();

        public Task SendConfirmedAsync(
            string email,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
