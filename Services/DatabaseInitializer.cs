using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
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
                    password_reset_expires TIMESTAMP
                );",

                "ALTER TABLE users DROP COLUMN IF EXISTS role;",

                @"CREATE TABLE IF NOT EXISTS user_sessions (
                    id SERIAL PRIMARY KEY,
                    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
                    token VARCHAR(255) UNIQUE NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    expires_at TIMESTAMP NOT NULL
                );",

                @"CREATE INDEX IF NOT EXISTS idx_user_sessions_token ON user_sessions(token);",

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
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );",

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
                
                "UPDATE users SET is_active = true WHERE is_active IS NULL;",
                "UPDATE users SET email_confirmation_resend_count = 0 WHERE email_confirmation_resend_count IS NULL;",
                @"UPDATE users
                  SET email_confirmation_next_send_at = created_at::date + INTERVAL '1 day 11 hours'
                  WHERE email_confirmed = false
                    AND email_confirmation_token IS NOT NULL
                    AND COALESCE(email_confirmation_resend_count, 0) < 2
                    AND email_confirmation_next_send_at IS NULL;",
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
    }
}
