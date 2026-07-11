using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PremierAPI.Services
{
    public class WhatsAppTemplateService
    {
        public const string PaymentApprovedClient = "payment_approved_client";
        public const string PaymentApprovedAdmin = "payment_approved_admin";
        private const string DefaultVariablesCsv = "cliente_nome,cliente_whatsapp,cliente_email,plano,dias,valor,computadores,slots,pedido_id,ambiente,data_pagamento";

        private readonly string _connectionString;

        public WhatsAppTemplateService(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("DefaultConnection") ?? "";
        }

        public static IReadOnlyList<WhatsAppTemplateDefinition> Definitions { get; } = new List<WhatsAppTemplateDefinition>
        {
            new(PaymentApprovedClient, "Pagamento aprovado - Cliente", "Cliente", "Enviada ao cliente quando o webhook da Asaas confirma o pagamento do PIX.", @"✅ *Pagamento Aprovado!*
Ola, {{cliente_nome}}!
Recebemos seu pagamento do plano {{plano}} ({{dias}} dias).

Nossa equipe tecnica ja foi notificada e vai entrar em contato pelo WhatsApp {{cliente_whatsapp}} para realizar a configuracao.

Obrigado por escolher a Premier Host!", DefaultVariablesCsv),
            new(PaymentApprovedAdmin, "Pagamento confirmado - Admin", "Administrador", "Enviada ao numero administrativo quando o webhook da Asaas confirma o pagamento do PIX.", @"💰 *Pagamento Confirmado!*
Ambiente: {{ambiente}}
Pedido: {{pedido_id}}
Cliente: {{cliente_nome}}
WhatsApp: {{cliente_whatsapp}}
Plano: {{plano}} - {{computadores}} PC(s) / {{slots}} slot(s)
Valor: R$ {{valor}}
Momento: pagamento confirmado pela Asaas

Configure o AnyDesk e finalize a entrega.", DefaultVariablesCsv)
        };

        public static WhatsAppTemplateDefinition? GetDefinition(string key)
        {
            return Definitions.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public static async Task SeedAsync(NpgsqlConnection conn)
        {
            foreach (var template in Definitions)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO whatsapp_message_templates (key, title, audience, trigger_description, body, default_body, variables_csv)
                    VALUES (@Key, @Title, @Audience, @TriggerDescription, @DefaultBody, @DefaultBody, @VariablesCsv)
                    ON CONFLICT (key) DO UPDATE SET
                        title = EXCLUDED.title,
                        audience = EXCLUDED.audience,
                        trigger_description = EXCLUDED.trigger_description,
                        default_body = EXCLUDED.default_body,
                        variables_csv = EXCLUDED.variables_csv;",
                    template);
            }
        }

        public async Task<IReadOnlyList<WhatsAppTemplateDto>> GetTemplatesAsync()
        {
            using var conn = new NpgsqlConnection(_connectionString);
            var rows = await conn.QueryAsync<WhatsAppTemplateRow>(@"
                SELECT key AS Key, title AS Title, audience AS Audience, trigger_description AS TriggerDescription,
                       body AS Body, default_body AS DefaultBody, variables_csv AS VariablesCsv, updated_at AS UpdatedAt
                FROM whatsapp_message_templates
                ORDER BY title;");

            return rows.Select(ToDto)
                .OrderBy(x => x.IsSystem ? Array.FindIndex(Definitions.ToArray(), d => d.Key == x.Key) : 999)
                .ThenBy(x => x.Title)
                .ToList();
        }

        public async Task<WhatsAppTemplateDto?> GetTemplateAsync(string key)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            var row = await conn.QueryFirstOrDefaultAsync<WhatsAppTemplateRow>(@"
                SELECT key AS Key, title AS Title, audience AS Audience, trigger_description AS TriggerDescription,
                       body AS Body, default_body AS DefaultBody, variables_csv AS VariablesCsv, updated_at AS UpdatedAt
                FROM whatsapp_message_templates
                WHERE key = @Key;", new { Key = key });

            return row == null ? null : ToDto(row);
        }

        public async Task<WhatsAppTemplateDto?> CreateTemplateAsync(string title, string audience, string triggerDescription, string body)
        {
            title = (title ?? "").Trim();
            audience = string.IsNullOrWhiteSpace(audience) ? "Personalizada" : audience.Trim();
            triggerDescription = string.IsNullOrWhiteSpace(triggerDescription)
                ? "Mensagem personalizada criada no painel. Para envio automatico, vincule esta chave no backend."
                : triggerDescription.Trim();
            body = (body ?? "").TrimEnd();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return null;

            using var conn = new NpgsqlConnection(_connectionString);
            var key = await BuildUniqueKeyAsync(conn, title);
            await conn.ExecuteAsync(@"
                INSERT INTO whatsapp_message_templates (key, title, audience, trigger_description, body, default_body, variables_csv, updated_at)
                VALUES (@Key, @Title, @Audience, @TriggerDescription, @Body, @Body, @VariablesCsv, CURRENT_TIMESTAMP);",
                new { Key = key, Title = title, Audience = audience, TriggerDescription = triggerDescription, Body = body, VariablesCsv = DefaultVariablesCsv });
            return await GetTemplateAsync(key);
        }

        public async Task<WhatsAppTemplateDto?> UpdateTemplateAsync(string key, string body)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            var existing = await conn.QueryFirstOrDefaultAsync<string>("SELECT key FROM whatsapp_message_templates WHERE key = @Key", new { Key = key });
            if (existing == null) return null;
            await conn.ExecuteAsync(@"UPDATE whatsapp_message_templates SET body = @Body, updated_at = CURRENT_TIMESTAMP WHERE key = @Key;", new { Key = key, Body = body });
            return await GetTemplateAsync(key);
        }

        public async Task<WhatsAppTemplateDto?> ResetTemplateAsync(string key)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            var existing = await conn.QueryFirstOrDefaultAsync<string>("SELECT key FROM whatsapp_message_templates WHERE key = @Key", new { Key = key });
            if (existing == null) return null;
            await conn.ExecuteAsync(@"UPDATE whatsapp_message_templates SET body = default_body, updated_at = CURRENT_TIMESTAMP WHERE key = @Key;", new { Key = key });
            return await GetTemplateAsync(key);
        }

        public async Task<string> RenderAsync(string key, IReadOnlyDictionary<string, string> variables)
        {
            var template = await GetTemplateAsync(key);
            var body = template?.Body ?? GetDefinition(key)?.DefaultBody ?? "";
            foreach (var item in variables)
            {
                body = body.Replace("{{" + item.Key + "}}", item.Value ?? "", StringComparison.OrdinalIgnoreCase);
            }
            return body;
        }

        public static Dictionary<string, string> BuildPaymentVariables(string envName, string paymentId, string clientName, string clientPhone, string clientEmail, string period, int days, decimal totalPrice, int computers, int wydsPerComputer)
        {
            return new Dictionary<string, string>
            {
                ["ambiente"] = envName, ["pedido_id"] = paymentId, ["cliente_nome"] = clientName,
                ["cliente_whatsapp"] = clientPhone, ["cliente_email"] = clientEmail, ["plano"] = period,
                ["dias"] = days.ToString(CultureInfo.InvariantCulture), ["valor"] = totalPrice.ToString("N2", new CultureInfo("pt-BR")),
                ["computadores"] = computers.ToString(CultureInfo.InvariantCulture), ["slots"] = wydsPerComputer.ToString(CultureInfo.InvariantCulture),
                ["data_pagamento"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm", new CultureInfo("pt-BR"))
            };
        }

        private static async Task<string> BuildUniqueKeyAsync(NpgsqlConnection conn, string title)
        {
            var baseKey = Regex.Replace(title.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD), @"[^a-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(baseKey)) baseKey = "mensagem";
            if (!baseKey.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)) baseKey = "custom_" + baseKey;
            if (baseKey.Length > 64) baseKey = baseKey[..64].Trim('_');
            var key = baseKey;
            var suffix = 2;
            while (await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM whatsapp_message_templates WHERE key = @Key", new { Key = key }) > 0)
            {
                key = $"{baseKey}_{suffix++}";
            }
            return key;
        }

        private static WhatsAppTemplateDto ToDto(WhatsAppTemplateRow row)
        {
            var variables = (row.VariablesCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isSystem = GetDefinition(row.Key) != null;
            return new WhatsAppTemplateDto(row.Key, row.Title, row.Audience, row.TriggerDescription, row.Body, row.DefaultBody, variables, row.UpdatedAt, isSystem, isSystem ? "Usada automaticamente pelo backend" : "Personalizada - ainda nao possui disparo automatico");
        }

        private class WhatsAppTemplateRow
        {
            public string Key { get; set; } = "";
            public string Title { get; set; } = "";
            public string Audience { get; set; } = "";
            public string TriggerDescription { get; set; } = "";
            public string Body { get; set; } = "";
            public string DefaultBody { get; set; } = "";
            public string VariablesCsv { get; set; } = "";
            public DateTime UpdatedAt { get; set; }
        }
    }

    public record WhatsAppTemplateDefinition(string Key, string Title, string Audience, string TriggerDescription, string DefaultBody, string VariablesCsv);
    public record WhatsAppTemplateDto(string Key, string Title, string Audience, string TriggerDescription, string Body, string DefaultBody, string[] Variables, DateTime UpdatedAt, bool IsSystem, string Usage);
}
