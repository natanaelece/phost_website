using Dapper;
using Npgsql;

namespace PremierAPI.Services;

public sealed class MetaBusinessEventService
{
    private readonly string _connectionString;
    private readonly MetaConversionsOptions _options;
    private readonly MetaConversionsService _conversions;
    private readonly ILogger<MetaBusinessEventService> _logger;

    public MetaBusinessEventService(
        IConfiguration configuration,
        MetaConversionsOptions options,
        MetaConversionsService conversions,
        ILogger<MetaBusinessEventService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        _options = options;
        _conversions = conversions;
        _logger = logger;
    }

    public static string RegistrationEventId(Guid userId) => $"complete_registration_{userId:N}";
    public static string LeadEventId(Guid requestId) => $"lead_{requestId:N}";
    public static string StartTrialEventId(Guid requestId) => $"start_trial_{requestId:N}";
    public static string InitiateCheckoutEventId(Guid orderId) => $"initiate_checkout_{orderId:N}";
    public static string PurchaseEventId(Guid orderId) => $"purchase_{orderId:N}";

    public Task<MetaDeliveryResult> TrySendBrowserEventAsync(
        string eventName,
        string eventId,
        string eventSourceUrl,
        Guid attributionId,
        IReadOnlyDictionary<string, object?> customData,
        CancellationToken cancellationToken = default) =>
        TrySendAsync(
            eventName,
            eventId,
            eventSourceUrl,
            customData,
            "a.id = @AttributionId",
            new { AttributionId = attributionId },
            cancellationToken);

    public Task<MetaDeliveryResult> TrySendCompleteRegistrationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        TrySendAsync(
            "CompleteRegistration",
            RegistrationEventId(userId),
            "https://phost.pro/confirmar",
            new Dictionary<string, object?> { ["content_name"] = "Cadastro Premier Host" },
            @"a.id = (
                SELECT latest.id
                FROM meta_attributions latest
                WHERE latest.user_id = @UserId
                ORDER BY latest.updated_at DESC
                LIMIT 1
            )",
            new { UserId = userId },
            cancellationToken);

    public Task<MetaDeliveryResult> TrySendLeadAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        TrySendAsync(
            "Lead",
            LeadEventId(requestId),
            "https://phost.pro/painel",
            new Dictionary<string, object?> { ["content_name"] = "Teste gratuito Premier Host" },
            "f.id = @RequestId",
            new { RequestId = requestId },
            cancellationToken,
            "JOIN free_trial_requests f ON f.meta_attribution_id = a.id");

    public Task<MetaDeliveryResult> TrySendStartTrialAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        TrySendAsync(
            "StartTrial",
            StartTrialEventId(requestId),
            "https://phost.pro/painel",
            new Dictionary<string, object?> { ["content_name"] = "Teste gratuito Premier Host" },
            "f.id = @RequestId",
            new { RequestId = requestId },
            cancellationToken,
            "JOIN free_trial_requests f ON f.meta_attribution_id = a.id");

    public async Task<MetaDeliveryResult> TrySendInitiateCheckoutAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        CommerceEventRow? commerce = await GetCommerceEventAsync(orderId, usePaidAmount: false, cancellationToken);
        if (commerce == null) return new MetaDeliveryResult(MetaDeliveryStatus.SkippedWithoutConsent);

        return await TrySendAsync(
            "InitiateCheckout",
            InitiateCheckoutEventId(orderId),
            "https://phost.pro/painel",
            BuildCommerceCustomData(commerce, commerce.TotalPrice),
            "o.id = @OrderId",
            new { OrderId = orderId },
            cancellationToken,
            "JOIN orders o ON o.meta_attribution_id = a.id");
    }

    public async Task<MetaDeliveryResult> TrySendPurchaseAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        CommerceEventRow? commerce = await GetCommerceEventAsync(orderId, usePaidAmount: true, cancellationToken);
        if (commerce?.PaidAmount is not > 0)
        {
            _logger.LogError(
                "[META CAPI] Purchase {EventId} sem valor efetivamente pago; evento não enviado.",
                PurchaseEventId(orderId));
            return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
        }

        return await TrySendAsync(
            "Purchase",
            PurchaseEventId(orderId),
            "https://phost.pro/painel",
            BuildCommerceCustomData(commerce, commerce.PaidAmount.Value),
            "o.id = @OrderId",
            new { OrderId = orderId },
            cancellationToken,
            "JOIN orders o ON o.meta_attribution_id = a.id");
    }

    public static IReadOnlyDictionary<string, object?> BuildCommerceCustomData(
        Guid orderId,
        string period,
        int computers,
        decimal value)
    {
        string contentId = $"premier_host_{period.Trim().ToLowerInvariant()}";
        return new Dictionary<string, object?>
        {
            ["currency"] = "BRL",
            ["value"] = value,
            ["order_id"] = orderId.ToString(),
            ["num_items"] = computers,
            ["content_name"] = "Plano Premier Host",
            ["content_type"] = "product",
            ["content_ids"] = new[] { contentId },
            ["contents"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = contentId,
                    ["quantity"] = computers,
                    ["item_price"] = computers > 0 ? decimal.Round(value / computers, 2) : value
                }
            }
        };
    }

    private static IReadOnlyDictionary<string, object?> BuildCommerceCustomData(
        CommerceEventRow commerce,
        decimal value) =>
        BuildCommerceCustomData(commerce.OrderId, commerce.Period, commerce.Computers, value);

    private async Task<CommerceEventRow?> GetCommerceEventAsync(
        Guid orderId,
        bool usePaidAmount,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = new NpgsqlConnection(_connectionString);
            return await db.QueryFirstOrDefaultAsync<CommerceEventRow>(new CommandDefinition($@"
                SELECT
                    id AS ""OrderId"",
                    period AS ""Period"",
                    computers AS ""Computers"",
                    total_price AS ""TotalPrice"",
                    paid_amount AS ""PaidAmount""
                FROM orders
                WHERE id = @OrderId
                  {(usePaidAmount ? "AND status = 'pago'" : "")};",
                new { OrderId = orderId },
                cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META CAPI] Falha ao preparar os dados comerciais do pedido {OrderId}.",
                orderId);
            return null;
        }
    }

    private async Task<MetaDeliveryResult> TrySendAsync(
        string eventName,
        string eventId,
        string eventSourceUrl,
        IReadOnlyDictionary<string, object?> customData,
        string attributionWhere,
        object parameters,
        CancellationToken cancellationToken,
        string additionalJoin = "")
    {
        try
        {
            await using var db = new NpgsqlConnection(_connectionString);
            MetaAttributionRow? row = await db.QueryFirstOrDefaultAsync<MetaAttributionRow>(
                new CommandDefinition($@"
                    SELECT
                        a.id AS ""AttributionId"",
                        a.consent_status AS ""ConsentStatus"",
                        a.consent_version AS ""ConsentVersion"",
                        a.client_ip_address::text AS ""ClientIpAddress"",
                        a.client_user_agent AS ""ClientUserAgent"",
                        a.fbp AS ""Fbp"",
                        a.fbc AS ""Fbc"",
                        u.id AS ""UserId"",
                        u.email AS ""Email"",
                        u.whatsapp AS ""Phone"",
                        u.name AS ""Name""
                    FROM meta_attributions a
                    {additionalJoin}
                    LEFT JOIN users u ON u.id = a.user_id
                    WHERE {attributionWhere}
                    LIMIT 1;",
                    parameters,
                    cancellationToken: cancellationToken));

            if (row == null)
                return new MetaDeliveryResult(MetaDeliveryStatus.SkippedWithoutConsent);

            var names = SplitName(row.Name);
            var attribution = new MetaAttributionContext(
                row.AttributionId,
                row.ConsentStatus == "accepted" && row.ConsentVersion == _options.ConsentVersion,
                row.ConsentVersion,
                row.ClientIpAddress,
                row.ClientUserAgent,
                row.Fbp,
                row.Fbc,
                row.Email,
                row.Phone,
                names.FirstName,
                names.LastName,
                row.UserId?.ToString());

            return await _conversions.SendAsync(
                new MetaConversionEvent(
                    eventName,
                    eventId,
                    eventSourceUrl,
                    attribution,
                    customData),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META CAPI] Falha ao preparar o evento {EventName} ({EventId}).",
                eventName,
                eventId);
            return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
        }
    }

    private static (string? FirstName, string? LastName) SplitName(string? fullName)
    {
        string[] parts = (fullName ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (null, null);
        return (parts[0], parts.Length > 1 ? parts[^1] : null);
    }

    private sealed class MetaAttributionRow
    {
        public Guid AttributionId { get; set; }
        public string ConsentStatus { get; set; } = "";
        public string ConsentVersion { get; set; } = "";
        public string? ClientIpAddress { get; set; }
        public string? ClientUserAgent { get; set; }
        public string? Fbp { get; set; }
        public string? Fbc { get; set; }
        public Guid? UserId { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Name { get; set; }
    }

    private sealed class CommerceEventRow
    {
        public Guid OrderId { get; set; }
        public string Period { get; set; } = "";
        public int Computers { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal? PaidAmount { get; set; }
    }
}
