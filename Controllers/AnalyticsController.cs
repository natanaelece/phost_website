using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/analytics")]
    [EnableRateLimiting("ApiPadrao")]
    public class AnalyticsController : ControllerBase
    {
        private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
        {
            "landing_viewed", "plan_cta_clicked", "simulator_viewed", "simulation_changed",
            "auth_opened", "signup_started", "signup_completed", "email_confirmed",
            "checkout_attempted", "pix_created", "pix_copied", "payment_received",
            "access_delivered", "checkout_error", "renewal_started", "renewal_paid"
        };

        private static readonly Regex PropertyKeyPattern = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);
        private readonly string _connectionString;

        public AnalyticsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        [HttpPost("events")]
        public async Task<IActionResult> Track([FromBody] AnalyticsEventRequest request)
        {
            if (!AllowedEvents.Contains(request.EventName))
                return BadRequest(new { erro = "Evento de analytics inválido." });
            if (request.SessionId == Guid.Empty)
                return BadRequest(new { erro = "Sessão de analytics inválida." });

            string pagePath = NormalizePagePath(request.PagePath);
            var safeProperties = SanitizeProperties(request.Properties);

            using var db = new NpgsqlConnection(_connectionString);
            Guid? verifiedUserId = await GetVerifiedUserId(db, request.UserId);
            string? referrerHost = NormalizeReferrerHost(request.Referrer);

            await db.ExecuteAsync(@"
                INSERT INTO product_analytics_events
                    (event_name, session_id, user_id, page_path, referrer_host, properties)
                VALUES
                    (@EventName, @SessionId, @UserId, @PagePath, @ReferrerHost, CAST(@Properties AS jsonb))",
                new
                {
                    request.EventName,
                    request.SessionId,
                    UserId = verifiedUserId,
                    PagePath = pagePath,
                    ReferrerHost = referrerHost,
                    Properties = JsonSerializer.Serialize(safeProperties)
                });

            return Accepted();
        }

        private async Task<Guid?> GetVerifiedUserId(NpgsqlConnection db, Guid? requestedUserId)
        {
            if (!requestedUserId.HasValue) return null;
            string? token = Request.Headers["X-Session-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null;

            int valid = await db.QueryFirstOrDefaultAsync<int>(@"
                SELECT 1
                FROM user_sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token = @Token AND s.user_id = @UserId
                  AND s.expires_at > @Now AND u.is_active = true",
                new { Token = token, UserId = requestedUserId.Value, Now = DateTime.UtcNow });

            return valid == 1 ? requestedUserId : null;
        }

        private static Dictionary<string, object?> SanitizeProperties(Dictionary<string, JsonElement>? properties)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (properties == null) return result;

            foreach (var pair in properties.Take(12))
            {
                if (!PropertyKeyPattern.IsMatch(pair.Key)) continue;
                object? value = pair.Value.ValueKind switch
                {
                    JsonValueKind.String => pair.Value.GetString()?[..Math.Min(pair.Value.GetString()!.Length, 80)],
                    JsonValueKind.Number when pair.Value.TryGetDecimal(out var number) => number,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
                if (value != null) result[pair.Key] = value;
            }
            return result;
        }

        private static string NormalizePagePath(string? pagePath)
        {
            string path = string.IsNullOrWhiteSpace(pagePath) ? "/" : pagePath.Trim();
            if (!path.StartsWith('/')) path = "/";
            return path[..Math.Min(path.Length, 200)];
        }

        private static string? NormalizeReferrerHost(string? referrer)
        {
            if (!Uri.TryCreate(referrer, UriKind.Absolute, out var uri)) return null;
            return uri.Host[..Math.Min(uri.Host.Length, 150)];
        }
    }

    public sealed class AnalyticsEventRequest
    {
        public string EventName { get; set; } = "";
        public Guid SessionId { get; set; }
        public Guid? UserId { get; set; }
        public string? PagePath { get; set; }
        public string? Referrer { get; set; }
        public Dictionary<string, JsonElement>? Properties { get; set; }
    }
}
