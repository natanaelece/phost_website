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
                    ad_username VARCHAR(100),
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

                @"CREATE TABLE IF NOT EXISTS orders (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    user_id UUID REFERENCES users(id) ON DELETE CASCADE,
                    anydesk_id VARCHAR(50),
                    computers INT,
                    wyds_per_computer INT,
                    period VARCHAR(20),
                    days INT,
                    total_price DECIMAL(10,2),
                    asaas_payment_id VARCHAR(100),
                    status VARCHAR(20),
                    delivered BOOLEAN DEFAULT false,
                    delivered_at TIMESTAMP,
                    paid_manually BOOLEAN DEFAULT false,
                    manual_paid_at TIMESTAMP,
                    ad_expiration_processed BOOLEAN DEFAULT false,
                    ad_expiration_processed_at TIMESTAMP,
                    ad_missing_link_alerted BOOLEAN DEFAULT false,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );",

                "ALTER TABLE users ADD COLUMN IF NOT EXISTS is_active BOOLEAN DEFAULT true;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS refunded BOOLEAN DEFAULT false;",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_confirmation_token VARCHAR(255);",
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS ad_username VARCHAR(100);",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS delivered_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_manually BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS manual_paid_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_expiration_processed BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_expiration_processed_at TIMESTAMP;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS ad_missing_link_alerted BOOLEAN DEFAULT false;",
                "ALTER TABLE orders ADD COLUMN IF NOT EXISTS canceled_at TIMESTAMP;",
                
                "UPDATE users SET is_active = true WHERE is_active IS NULL;",
                "UPDATE orders SET delivered = false WHERE delivered IS NULL;",
                "UPDATE orders SET paid_manually = false WHERE paid_manually IS NULL;",
                "UPDATE orders SET ad_expiration_processed = false WHERE ad_expiration_processed IS NULL;",
                "UPDATE orders SET ad_missing_link_alerted = false WHERE ad_missing_link_alerted IS NULL;"
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

            // Realiza o backup da base de dados sempre que inicializa
            BackupDatabase(connString);
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
    }
}
