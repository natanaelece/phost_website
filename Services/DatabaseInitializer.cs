using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace PremierAPI.Services
{
    public static class DatabaseInitializer
    {
        public static void Initialize(IConfiguration config)
        {
            var connString = config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connString)) return;

            using var conn = new NpgsqlConnection(connString);
            conn.Open();

            CheckDatabaseEncoding(conn);

            string[] sqlCommands = new string[]
            {
                @"CREATE TABLE IF NOT EXISTS users (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    name VARCHAR(100) NOT NULL,
                    email VARCHAR(150) UNIQUE NOT NULL,
                    whatsapp VARCHAR(20),
                    password_hash VARCHAR(255) NOT NULL,
                    is_active BOOLEAN DEFAULT true,
                    email_confirmed BOOLEAN DEFAULT false,
                    email_confirmation_token VARCHAR(255),
                    email_confirmation_resend_count INT DEFAULT 0,
                    email_confirmation_last_sent_at TIMESTAMP,
                    email_confirmation_next_send_at TIMESTAMP,
                    ad_username VARCHAR(100),
                    ad_credentials_delivered_at TIMESTAMP,
                    referral_code VARCHAR(20) UNIQUE,
                    referred_by UUID REFERENCES users(id),
                    used_referral_discount BOOLEAN DEFAULT false,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    password_reset_token VARCHAR(255),
                    password_reset_token_hash CHAR(64),
                    password_reset_expires TIMESTAMP
                );",

                "ALTER TABLE users DROP COLUMN IF EXISTS role;",

                @"CREATE TABLE IF NOT EXISTS meta_attributions (
                    id UUID PRIMARY KEY,
                    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
                    source_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL,
                    consent_status VARCHAR(10) NOT NULL
                        CHECK (consent_status IN ('accepted', 'rejected')),
                    consent_version VARCHAR(20) NOT NULL,
                    consented_at TIMESTAMP,
                    revoked_at TIMESTAMP,
                    fbp VARCHAR(255),
                    fbc VARCHAR(500),
                    fbclid VARCHAR(500),
                    client_ip_address INET,
                    client_user_agent VARCHAR(512),
                    source_url VARCHAR(500),
                    captured_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                @"CREATE TABLE IF NOT EXISTS meta_conversion_events (
                    event_id VARCHAR(100) PRIMARY KEY,
                    event_name VARCHAR(60) NOT NULL,
                    delivery_status VARCHAR(20) NOT NULL
                        CHECK (delivery_status IN ('processing', 'sent', 'failed')),
                    attempt_count INT NOT NULL DEFAULT 1,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    last_attempt_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    sent_at TIMESTAMP
                );",

                @"CREATE TABLE IF NOT EXISTS user_sessions (
                    id SERIAL PRIMARY KEY,
                    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
                    token VARCHAR(255) UNIQUE,
                    token_hash CHAR(64) NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    expires_at TIMESTAMP NOT NULL
                );",

                @"ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS token_hash CHAR(64);",
                @"CREATE UNIQUE INDEX IF NOT EXISTS idx_user_sessions_token_hash ON user_sessions(token_hash);",

                @"CREATE TABLE IF NOT EXISTS email_confirmation_tokens (
                    id BIGSERIAL PRIMARY KEY,
                    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    token_hash CHAR(64) NOT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    expires_at TIMESTAMP NOT NULL,
                    sent_at TIMESTAMP,
                    used_at TIMESTAMP,
                    delivery_kind VARCHAR(20) NOT NULL DEFAULT 'migrated'
                        CHECK (delivery_kind IN ('initial', 'reminder', 'manual', 'migrated')),
                    claim_expires_at TIMESTAMP,
                    failed_at TIMESTAMP,
                    sanitized_failure_code VARCHAR(40)
                );",
                @"CREATE UNIQUE INDEX IF NOT EXISTS idx_email_confirmation_tokens_hash
                   ON email_confirmation_tokens(token_hash);",
                @"CREATE INDEX IF NOT EXISTS idx_email_confirmation_tokens_user_active
                   ON email_confirmation_tokens(user_id, expires_at)
                   WHERE used_at IS NULL;",
                @"CREATE INDEX IF NOT EXISTS idx_email_confirmation_tokens_pending_claim
                   ON email_confirmation_tokens(claim_expires_at)
                   WHERE sent_at IS NULL AND failed_at IS NULL;",

                @"CREATE TABLE IF NOT EXISTS pending_ad_credentials (
                    user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                    protected_password TEXT NOT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                @"CREATE TABLE IF NOT EXISTS coupons (
                    id SERIAL PRIMARY KEY,
                    code VARCHAR(20) UNIQUE NOT NULL,
                    discount_type VARCHAR(10) NOT NULL CHECK (discount_type IN ('percent', 'fixed')),
                    discount_value DECIMAL(10,2) NOT NULL,
                    is_active BOOLEAN DEFAULT true,
                    max_uses INT,
                    uses INT DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );",

                @"INSERT INTO coupons
                    (code, discount_type, discount_value, is_active, max_uses, uses, created_at)
                  VALUES
                    ('PROMO10', 'percent', 10.00, true, NULL, 0, TIMESTAMP '2026-07-05 14:48:23.512924'),
                    ('BETA15', 'fixed', 15.00, true, NULL, 0, TIMESTAMP '2026-07-05 14:48:23.512924')
                  ON CONFLICT (code) DO NOTHING;",

                @"CREATE TABLE IF NOT EXISTS orders (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
                    anydesk_id VARCHAR(50),
                    wyd_server_name VARCHAR(50),
                    computers INT,
                    wyds_per_computer INT,
                    period VARCHAR(20),
                    days INT,
                    total_price DECIMAL(10,2),
                    paid_amount DECIMAL(10,2),
                    paid_at TIMESTAMP,
                    asaas_payment_id VARCHAR(100),
                    asaas_customer_id VARCHAR(100),
                    asaas_pix_qr_code_id VARCHAR(100),
                    pix_payload TEXT,
                    pix_encoded_image TEXT,
                    pix_expires_at TIMESTAMP,
                    status VARCHAR(20),
                    delivered BOOLEAN DEFAULT false,
                    delivered_at TIMESTAMP,
                    paid_manually BOOLEAN DEFAULT false,
                    created_manually BOOLEAN DEFAULT false,
                    manual_paid_at TIMESTAMP,
                    canceled_was_paid BOOLEAN DEFAULT false,
                    ad_expiration_processed BOOLEAN DEFAULT false,
                    ad_expiration_processed_at TIMESTAMP,
                    ad_missing_link_alerted BOOLEAN DEFAULT false,
                    ad_provisioned_at TIMESTAMP,
                    ad_provisioning_attempts INT DEFAULT 0,
                    ad_provisioning_error VARCHAR(500),
                    ad_provisioning_next_attempt_at TIMESTAMP,
                    meta_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );",

                "ALTER TABLE users ADD COLUMN IF NOT EXISTS is_active BOOLEAN DEFAULT true;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_ip INET;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_user_agent VARCHAR(512);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_accept_language VARCHAR(200);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_country_code VARCHAR(2);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_referrer_host VARCHAR(150);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS registration_source VARCHAR(30);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS refunded BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS asaas_customer_id VARCHAR(100);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS asaas_pix_qr_code_id VARCHAR(100);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS pix_payload TEXT;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS pix_encoded_image TEXT;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS pix_expires_at TIMESTAMP;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_confirmation_token VARCHAR(255);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_token VARCHAR(255);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_token_hash CHAR(64);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_expires TIMESTAMP;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_confirmation_resend_count INT DEFAULT 0;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_confirmation_last_sent_at TIMESTAMP;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_confirmation_next_send_at TIMESTAMP;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS ad_username VARCHAR(100);",
                @"DO $$
                  BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'ad_credentials_delivered_at'
                    ) THEN
                        ALTER TABLE users ADD COLUMN ad_credentials_delivered_at TIMESTAMP;
                        UPDATE users SET ad_credentials_delivered_at = CURRENT_TIMESTAMP WHERE ad_username IS NOT NULL;
                    END IF;
                  END $$;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_manually BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS created_manually BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS manual_paid_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS canceled_was_paid BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_expiration_processed BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_expiration_processed_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_missing_link_alerted BOOLEAN DEFAULT false;",
                @"DO $$
                  BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'orders' AND column_name = 'ad_provisioned_at'
                    ) THEN
                        ALTER TABLE orders ADD COLUMN ad_provisioned_at TIMESTAMP;
                        UPDATE orders SET ad_provisioned_at = CURRENT_TIMESTAMP WHERE status = 'pago';
                    END IF;
                  END $$;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_provisioning_attempts INT DEFAULT 0;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_provisioning_error VARCHAR(500);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_provisioning_next_attempt_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS canceled_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS wyd_server_name VARCHAR(50);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_amount DECIMAL(10,2);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS meta_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL;",
                "ALTER TABLE meta_attributions ADD COLUMN IF NOT EXISTS source_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL;",

                "CREATE UNIQUE INDEX IF NOT EXISTS idx_orders_asaas_pix_qr_code_id ON orders(asaas_pix_qr_code_id) WHERE asaas_pix_qr_code_id IS NOT NULL;",
                "CREATE INDEX IF NOT EXISTS idx_orders_ad_provisioning_pending ON orders(ad_provisioning_next_attempt_at, created_at) WHERE status = 'pago' AND ad_provisioned_at IS NULL;",
                "CREATE INDEX IF NOT EXISTS idx_users_email_confirmation_pending ON users(email_confirmation_next_send_at) WHERE email_confirmed = false;",

                @"CREATE TABLE IF NOT EXISTS product_analytics_events (
                    id BIGSERIAL PRIMARY KEY,
                    event_name VARCHAR(60) NOT NULL,
                    session_id UUID NOT NULL,
                    user_id UUID REFERENCES users(id) ON DELETE SET NULL,
                    page_path VARCHAR(200) NOT NULL,
                    referrer_host VARCHAR(150),
                    properties JSONB NOT NULL DEFAULT '{}'::jsonb,
                    occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                "CREATE INDEX IF NOT EXISTS idx_analytics_events_name_date ON product_analytics_events(event_name, occurred_at DESC);",
                "CREATE INDEX IF NOT EXISTS idx_analytics_events_session_date ON product_analytics_events(session_id, occurred_at DESC);",
                "CREATE INDEX IF NOT EXISTS idx_analytics_events_user_date ON product_analytics_events(user_id, occurred_at DESC) WHERE user_id IS NOT NULL;",
                "DELETE FROM product_analytics_events WHERE occurred_at < CURRENT_TIMESTAMP - INTERVAL '13 months';",

                @"CREATE TABLE IF NOT EXISTS free_trial_requests (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    user_id UUID NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
                    status VARCHAR(20) NOT NULL DEFAULT 'solicitado'
                        CHECK (status IN ('solicitado', 'liberado', 'utilizado', 'recusado', 'cancelado')),
                    first_requested_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    last_requested_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    request_count INT NOT NULL DEFAULT 1 CHECK (request_count > 0),
                    released_at TIMESTAMP,
                    used_at TIMESTAMP,
                    closed_at TIMESTAMP,
                    released_by VARCHAR(150),
                    meta_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                "ALTER TABLE free_trial_requests ADD COLUMN IF NOT EXISTS meta_attribution_id UUID REFERENCES meta_attributions(id) ON DELETE SET NULL;",

                @"CREATE TABLE IF NOT EXISTS free_trial_events (
                    id BIGSERIAL PRIMARY KEY,
                    free_trial_request_id UUID NOT NULL REFERENCES free_trial_requests(id) ON DELETE CASCADE,
                    event_type VARCHAR(20) NOT NULL
                        CHECK (event_type IN ('solicitado', 'liberado', 'utilizado', 'recusado', 'cancelado')),
                    actor_type VARCHAR(20) NOT NULL CHECK (actor_type IN ('usuario', 'admin', 'sistema')),
                    actor_identifier VARCHAR(150),
                    occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                "CREATE INDEX IF NOT EXISTS idx_free_trial_requests_status_date ON free_trial_requests(status, last_requested_at DESC);",
                "CREATE INDEX IF NOT EXISTS idx_free_trial_requests_used_at ON free_trial_requests(used_at DESC) WHERE used_at IS NOT NULL;",
                "CREATE INDEX IF NOT EXISTS idx_free_trial_events_request_date ON free_trial_events(free_trial_request_id, occurred_at DESC);",

                @"CREATE TABLE IF NOT EXISTS user_activity_events (
                    id BIGSERIAL PRIMARY KEY,
                    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    event_type VARCHAR(20) NOT NULL CHECK (event_type IN ('cadastro', 'login', 'logout')),
                    ip_address INET,
                    user_agent VARCHAR(512),
                    accept_language VARCHAR(200),
                    country_code VARCHAR(2),
                    referrer_host VARCHAR(150),
                    occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

                "CREATE INDEX IF NOT EXISTS idx_user_activity_events_type_date ON user_activity_events(event_type, occurred_at DESC);",
                "CREATE INDEX IF NOT EXISTS idx_user_activity_events_user_date ON user_activity_events(user_id, occurred_at DESC);",
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_user_activity_events_single_registration ON user_activity_events(user_id) WHERE event_type = 'cadastro';",
                @"INSERT INTO user_activity_events
                    (user_id, event_type, ip_address, user_agent, accept_language, country_code, referrer_host, occurred_at)
                  SELECT
                    u.id, 'cadastro', u.registration_ip, u.registration_user_agent,
                    u.registration_accept_language, u.registration_country_code,
                    u.registration_referrer_host, COALESCE(u.created_at, CURRENT_TIMESTAMP)
                  FROM users u
                  WHERE NOT EXISTS (
                    SELECT 1
                    FROM user_activity_events e
                    WHERE e.user_id = u.id AND e.event_type = 'cadastro'
                  );",

                "CREATE INDEX IF NOT EXISTS idx_meta_attributions_user_date ON meta_attributions(user_id, updated_at DESC) WHERE user_id IS NOT NULL;",
                "CREATE INDEX IF NOT EXISTS idx_meta_attributions_source ON meta_attributions(source_attribution_id) WHERE source_attribution_id IS NOT NULL;",
                "CREATE INDEX IF NOT EXISTS idx_meta_attributions_consent_date ON meta_attributions(consent_status, updated_at DESC);",
                "CREATE INDEX IF NOT EXISTS idx_meta_conversion_events_status_date ON meta_conversion_events(delivery_status, last_attempt_at DESC);",
                "DELETE FROM meta_conversion_events WHERE created_at < CURRENT_TIMESTAMP - INTERVAL '13 months';",
                "DELETE FROM meta_attributions WHERE updated_at < CURRENT_TIMESTAMP - INTERVAL '13 months';",
                
                "UPDATE users SET is_active = true WHERE is_active IS NULL;",
                "UPDATE users SET email_confirmation_resend_count = 0 WHERE email_confirmation_resend_count IS NULL;",
                @"UPDATE users
                  SET email_confirmation_next_send_at = created_at::date + INTERVAL '1 day 11 hours'
                  WHERE email_confirmed = false
                    AND COALESCE(email_confirmation_resend_count, 0) < 2
                    AND email_confirmation_next_send_at IS NULL
                    AND EXISTS (
                        SELECT 1
                        FROM email_confirmation_tokens t
                        WHERE t.user_id = users.id
                          AND t.used_at IS NULL
                    );",
                "UPDATE orders SET delivered = false WHERE delivered IS NULL;",
                "UPDATE orders SET paid_manually = false WHERE paid_manually IS NULL;",
                @"UPDATE orders
                  SET canceled_was_paid = true
                  WHERE status = 'cancelado'
                    AND COALESCE(canceled_was_paid, false) = false
                    AND (
                        COALESCE(refunded, false) = true
                        OR COALESCE(paid_manually, false) = true
                        OR manual_paid_at IS NOT NULL
                        OR ad_provisioned_at IS NOT NULL
                        OR (asaas_pix_qr_code_id IS NOT NULL AND asaas_payment_id IS NOT NULL)
                    );",
                "UPDATE orders SET ad_expiration_processed = false WHERE ad_expiration_processed IS NULL;",
                "UPDATE orders SET ad_missing_link_alerted = false WHERE ad_missing_link_alerted IS NULL;",
                "UPDATE orders SET ad_provisioning_attempts = 0 WHERE ad_provisioning_attempts IS NULL;",

                @"CREATE TABLE IF NOT EXISTS whatsapp_message_templates (
                    key VARCHAR(80) PRIMARY KEY,
                    title VARCHAR(120) NOT NULL,
                    audience VARCHAR(60) NOT NULL,
                    trigger_description TEXT NOT NULL,
                    body TEXT NOT NULL,
                    default_body TEXT NOT NULL,
                    variables_csv TEXT NOT NULL,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );"
            };

            foreach (var query in sqlCommands)
            {
                try
                {
                    using var cmd = new NpgsqlCommand(query, conn);
                    cmd.ExecuteNonQuery();
                }
                catch (System.Exception ex)
                {
                    string logQuery = query.Length > 50 ? query.Substring(0, 50).Replace('\n', ' ') + "..." : query;
                    System.Console.WriteLine($"[DB INIT AVISO] Falha ao rodar '{logQuery}': {ex.Message}");
                }
            }

            MigrateClientAuthTokens(conn);

            try
            {
                WhatsAppTemplateService.SeedAsync(conn).GetAwaiter().GetResult();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[DB INIT AVISO] Falha ao preparar templates WhatsApp: {ex.Message}");
            }

            // Realiza o backup da base de dados sempre que inicializa
            BackupDatabase(connString);
        }

        internal static void MigrateClientAuthTokens(NpgsqlConnection conn)
        {
            using var transaction = conn.BeginTransaction();
            try
            {
                conn.Execute(
                    "ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS token_hash CHAR(64);",
                    transaction: transaction);
                conn.Execute(
                    "ALTER TABLE user_sessions ALTER COLUMN token DROP NOT NULL;",
                    transaction: transaction);
                conn.Execute(
                    "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_token_hash CHAR(64);",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS email_confirmation_tokens (
                        id BIGSERIAL PRIMARY KEY,
                        user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                        token_hash CHAR(64) NOT NULL,
                        created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        expires_at TIMESTAMP NOT NULL,
                        sent_at TIMESTAMP,
                        used_at TIMESTAMP,
                        delivery_kind VARCHAR(20) NOT NULL DEFAULT 'migrated'
                            CHECK (delivery_kind IN ('initial', 'reminder', 'manual', 'migrated')),
                        claim_expires_at TIMESTAMP,
                        failed_at TIMESTAMP,
                        sanitized_failure_code VARCHAR(40)
                    );",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_email_confirmation_tokens_hash
                    ON email_confirmation_tokens(token_hash);",
                    transaction: transaction);

                BackfillSessionTokenHashes(conn, transaction);
                bool hasLegacyEmailHash = HasColumn(
                    conn,
                    transaction,
                    "users",
                    "email_confirmation_token_hash");
                BackfillUserTokens(
                    conn,
                    transaction,
                    hasLegacyEmailHash);

                long sessionsWithoutHash = conn.QuerySingle<long>(
                    "SELECT COUNT(*) FROM user_sessions WHERE token_hash IS NULL;",
                    transaction: transaction);
                long pendingResetWithoutHash = conn.QuerySingle<long>(@"
                    SELECT COUNT(*)
                    FROM users
                    WHERE password_reset_token IS NOT NULL
                      AND password_reset_token_hash IS NULL;",
                    transaction: transaction);

                if (sessionsWithoutHash != 0 || pendingResetWithoutHash != 0)
                {
                    throw new InvalidOperationException(
                        "A migração de tokens não conseguiu preencher todos os hashes.");
                }

                conn.Execute(
                    "UPDATE user_sessions SET token = NULL WHERE token IS NOT NULL;",
                    transaction: transaction);
                conn.Execute(@"
                    UPDATE users
                    SET email_confirmation_token = NULL,
                        password_reset_token = NULL
                    WHERE email_confirmation_token IS NOT NULL
                       OR password_reset_token IS NOT NULL;",
                    transaction: transaction);
                if (hasLegacyEmailHash)
                {
                    conn.Execute(
                        "UPDATE users SET email_confirmation_token_hash = NULL WHERE email_confirmation_token_hash IS NOT NULL;",
                        transaction: transaction);
                    conn.Execute(
                        "DROP INDEX IF EXISTS idx_users_email_confirmation_token_hash;",
                        transaction: transaction);
                }

                conn.Execute(
                    "ALTER TABLE user_sessions ALTER COLUMN token_hash SET NOT NULL;",
                    transaction: transaction);
                conn.Execute(
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_user_sessions_token_hash ON user_sessions(token_hash);",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_email_confirmation_tokens_hash
                    ON email_confirmation_tokens(token_hash);",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE INDEX IF NOT EXISTS idx_email_confirmation_tokens_user_active
                    ON email_confirmation_tokens(user_id, expires_at)
                    WHERE used_at IS NULL;",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE INDEX IF NOT EXISTS idx_email_confirmation_tokens_pending_claim
                    ON email_confirmation_tokens(claim_expires_at)
                    WHERE sent_at IS NULL AND failed_at IS NULL;",
                    transaction: transaction);
                conn.Execute(@"
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_password_reset_token_hash
                    ON users(password_reset_token_hash)
                    WHERE password_reset_token_hash IS NOT NULL;",
                    transaction: transaction);
                conn.Execute(@"
                    UPDATE users u
                    SET email_confirmation_next_send_at =
                        u.created_at::date + INTERVAL '1 day 11 hours'
                    WHERE u.email_confirmed = false
                      AND COALESCE(u.email_confirmation_resend_count, 0) < 2
                      AND u.email_confirmation_next_send_at IS NULL
                      AND EXISTS (
                          SELECT 1
                          FROM email_confirmation_tokens t
                          WHERE t.user_id = u.id
                            AND t.used_at IS NULL
                      );",
                    transaction: transaction);

                ClientAuthStorageStatus status =
                    GetClientAuthStorageStatus(conn, transaction);
                if (status.SessionsWithoutHash != 0 ||
                    status.LegacyTokensNotNull != 0 ||
                    status.InvalidHashes != 0)
                {
                    throw new InvalidOperationException(
                        "A verificação final do armazenamento de tokens falhou.");
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static void BackfillSessionTokenHashes(
            NpgsqlConnection conn,
            NpgsqlTransaction transaction)
        {
            var rows = conn.Query<LegacySessionToken>(@"
                SELECT id AS Id, token AS Token, token_hash AS TokenHash
                FROM user_sessions
                WHERE token IS NOT NULL;",
                transaction: transaction);

            foreach (LegacySessionToken row in rows)
            {
                string expectedHash = HashLegacyToken(row.Token);
                if (!string.IsNullOrWhiteSpace(row.TokenHash) &&
                    !string.Equals(
                        row.TokenHash,
                        expectedHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Uma sessão legada possui hash divergente.");
                }

                conn.Execute(@"
                    UPDATE user_sessions
                    SET token_hash = @TokenHash
                    WHERE id = @Id;",
                    new { row.Id, TokenHash = expectedHash },
                    transaction);
            }
        }

        private static void BackfillUserTokens(
            NpgsqlConnection conn,
            NpgsqlTransaction transaction,
            bool hasLegacyEmailHash)
        {
            string legacyEmailHashSelection = hasLegacyEmailHash
                ? "email_confirmation_token_hash"
                : "NULL::char(64)";
            string legacyEmailHashFilter = hasLegacyEmailHash
                ? "OR email_confirmation_token_hash IS NOT NULL"
                : string.Empty;
            var rows = conn.Query<LegacyUserTokens>($@"
                SELECT id AS Id,
                       email_confirmation_token AS EmailConfirmationToken,
                       {legacyEmailHashSelection} AS EmailConfirmationTokenHash,
                       password_reset_token AS PasswordResetToken,
                       password_reset_token_hash AS PasswordResetTokenHash,
                       email_confirmed AS EmailConfirmed,
                       created_at AS CreatedAt
                FROM users
                WHERE email_confirmation_token IS NOT NULL
                   OR password_reset_token IS NOT NULL
                   {legacyEmailHashFilter};",
                transaction: transaction);

            foreach (LegacyUserTokens row in rows)
            {
                if (!string.IsNullOrEmpty(row.EmailConfirmationToken))
                {
                    EnsureMigratedEmailConfirmationHash(
                        conn,
                        transaction,
                        row,
                        HashLegacyToken(row.EmailConfirmationToken));
                }
                if (!string.IsNullOrWhiteSpace(
                        row.EmailConfirmationTokenHash))
                {
                    if (!SecurityTokenService.IsValidHash(
                            row.EmailConfirmationTokenHash))
                    {
                        throw new InvalidOperationException(
                            "Um hash intermediário de confirmação é inválido.");
                    }
                    EnsureMigratedEmailConfirmationHash(
                        conn,
                        transaction,
                        row,
                        row.EmailConfirmationTokenHash);
                }

                string? resetHash = VerifyOrHashLegacyToken(
                    row.PasswordResetToken,
                    row.PasswordResetTokenHash,
                    "recuperação");

                conn.Execute(@"
                    UPDATE users
                    SET password_reset_token_hash =
                            COALESCE(@PasswordResetTokenHash, password_reset_token_hash)
                    WHERE id = @Id;",
                    new
                    {
                        row.Id,
                        PasswordResetTokenHash = resetHash
                    },
                    transaction);
            }
        }

        private static void EnsureMigratedEmailConfirmationHash(
            NpgsqlConnection conn,
            NpgsqlTransaction transaction,
            LegacyUserTokens row,
            string tokenHash)
        {
            DateTime createdAt = row.CreatedAt ?? DateTime.UtcNow;
            conn.Execute(@"
                INSERT INTO email_confirmation_tokens
                    (user_id, token_hash, created_at, expires_at,
                     sent_at, used_at, delivery_kind)
                VALUES
                    (@UserId, @TokenHash, @CreatedAt, 'infinity'::timestamp,
                     @CreatedAt,
                     CASE WHEN @EmailConfirmed THEN CURRENT_TIMESTAMP ELSE NULL END,
                     'migrated')
                ON CONFLICT (token_hash) DO NOTHING;",
                new
                {
                    UserId = row.Id,
                    TokenHash = tokenHash,
                    CreatedAt = createdAt,
                    row.EmailConfirmed
                },
                transaction);

            bool belongsToUser = conn.QuerySingle<bool>(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM email_confirmation_tokens
                    WHERE user_id = @UserId
                      AND token_hash = @TokenHash
                );",
                new { UserId = row.Id, TokenHash = tokenHash },
                transaction);
            if (!belongsToUser)
            {
                throw new InvalidOperationException(
                    "Um hash legado de confirmação está associado a outro usuário.");
            }
        }

        private static string? VerifyOrHashLegacyToken(
            string? rawToken,
            string? existingHash,
            string tokenKind)
        {
            if (string.IsNullOrEmpty(rawToken)) return null;
            string expectedHash = HashLegacyToken(rawToken);
            if (!string.IsNullOrWhiteSpace(existingHash) &&
                !string.Equals(existingHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Um token legado de {tokenKind} possui hash divergente.");
            }

            return expectedHash;
        }

        private static string HashLegacyToken(string rawToken) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)))
                .ToLowerInvariant();

        private static bool HasColumn(
            NpgsqlConnection conn,
            NpgsqlTransaction? transaction,
            string tableName,
            string columnName) =>
            conn.QuerySingle<bool>(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = @TableName
                      AND column_name = @ColumnName
                );",
                new { TableName = tableName, ColumnName = columnName },
                transaction);

        public static ClientAuthStorageStatus GetClientAuthStorageStatus(
            IConfiguration config)
        {
            string connectionString =
                config.GetConnectionString("DefaultConnection") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Banco de dados não configurado.");

            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();
            return GetClientAuthStorageStatus(conn, transaction: null);
        }

        private static ClientAuthStorageStatus GetClientAuthStorageStatus(
            NpgsqlConnection conn,
            NpgsqlTransaction? transaction)
        {
            bool hasLegacyEmailHash = HasColumn(
                conn,
                transaction,
                "users",
                "email_confirmation_token_hash");
            string legacyEmailHashCount = hasLegacyEmailHash
                ? @"
                        +
                        (SELECT COUNT(*)
                         FROM users
                         WHERE email_confirmation_token_hash IS NOT NULL)"
                : string.Empty;

            return conn.QuerySingle<ClientAuthStorageStatus>($@"
                SELECT
                    (SELECT COUNT(*)
                     FROM user_sessions
                     WHERE token_hash IS NULL) AS ""SessionsWithoutHash"",
                    (
                        (SELECT COUNT(*) FROM user_sessions WHERE token IS NOT NULL)
                        +
                        (SELECT COUNT(*)
                         FROM users
                         WHERE email_confirmation_token IS NOT NULL
                            OR password_reset_token IS NOT NULL)
                        {legacyEmailHashCount}
                    ) AS ""LegacyTokensNotNull"",
                    (
                        (SELECT COUNT(*)
                         FROM user_sessions
                         WHERE token_hash IS NOT NULL
                           AND token_hash !~ '^[0-9a-f]{{64}}$')
                        +
                        (SELECT COUNT(*)
                         FROM email_confirmation_tokens
                         WHERE token_hash !~ '^[0-9a-f]{{64}}$')
                        +
                        (SELECT COUNT(*)
                         FROM users
                         WHERE password_reset_token_hash IS NOT NULL
                           AND password_reset_token_hash !~ '^[0-9a-f]{{64}}$')
                    ) AS ""InvalidHashes"";",
                transaction: transaction);
        }

        private static void CheckDatabaseEncoding(NpgsqlConnection conn)
        {
            try
            {
                var info = conn.QueryFirstOrDefault<DatabaseEncodingInfo>(@"
                    SELECT
                        current_database() AS ""DatabaseName"",
                        pg_encoding_to_char(encoding) AS ""ServerEncoding"",
                        datcollate AS ""Collation"",
                        datctype AS ""CharacterType""
                    FROM pg_database
                    WHERE datname = current_database();");

                if (info == null) return;

                System.Console.WriteLine($"[DB ENCODING] Banco '{info.DatabaseName}' encoding={info.ServerEncoding}, collation={info.Collation}, ctype={info.CharacterType}");

                if (!string.Equals(info.ServerEncoding, "UTF8", System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(info.ServerEncoding, "UTF-8", System.StringComparison.OrdinalIgnoreCase))
                {
                    System.Console.WriteLine("[DB ENCODING AVISO CRITICO] O banco nao esta em UTF8. PostgreSQL nao permite converter encoding com ALTER DATABASE; para suporte confiavel a emoji e acentos, faca dump, crie um banco novo com ENCODING 'UTF8' e restaure nele. Consulte README.md > Encoding UTF-8 do PostgreSQL.");
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[DB ENCODING AVISO] Nao foi possivel validar o encoding do banco: {ex.Message}");
            }
        }

        private static void BackupDatabase(string connString)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connString);
                string dumpDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "dump_db");
                if (!System.IO.Directory.Exists(dumpDir))
                {
                    System.IO.Directory.CreateDirectory(dumpDir);
                }

                string fileName = $"backup_{System.DateTime.Now:yyyyMMdd_HHmmss}.sql";
                string filePath = System.IO.Path.Combine(dumpDir, fileName);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pg_dump",
                    Arguments = $"-h {builder.Host} -p {builder.Port} -U {builder.Username} -d {builder.Database} -f \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                // Define a senha no ambiente do processo para que o pg_dump conecte sem prompt
                psi.EnvironmentVariables["PGPASSWORD"] = builder.Password;

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        string err = process.StandardError.ReadToEnd();
                        System.Console.WriteLine($"[DB BACKUP ERRO] Falha no pg_dump: {err}");
                    }
                    else
                    {
                        System.Console.WriteLine($"[DB BACKUP] Backup salvo com sucesso: {fileName}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[DB BACKUP ERRO] Exceção ao tentar fazer backup: {ex.Message}");
            }
        }
        private sealed class DatabaseEncodingInfo
        {
            public string DatabaseName { get; set; } = "";
            public string ServerEncoding { get; set; } = "";
            public string Collation { get; set; } = "";
            public string CharacterType { get; set; } = "";
        }

        private sealed class LegacySessionToken
        {
            public long Id { get; set; }
            public string Token { get; set; } = "";
            public string? TokenHash { get; set; }
        }

        private sealed class LegacyUserTokens
        {
            public Guid Id { get; set; }
            public string? EmailConfirmationToken { get; set; }
            public string? EmailConfirmationTokenHash { get; set; }
            public string? PasswordResetToken { get; set; }
            public string? PasswordResetTokenHash { get; set; }
            public bool EmailConfirmed { get; set; }
            public DateTime? CreatedAt { get; set; }
        }
    }

    public sealed record ClientAuthStorageStatus(
        long SessionsWithoutHash,
        long LegacyTokensNotNull,
        long InvalidHashes);
}
