using Microsoft.Extensions.Logging.Abstractions;

namespace PremierAPI.Services;

internal static class MetaConversionsSmokeRunner
{
    internal const string Command = "--meta-capi-smoke";

    public static async Task<int> RunAsync(
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        MetaConversionsOptions options = MetaConversionsOptions.FromConfiguration(configuration);
        if (!options.IsConfigured)
        {
            await error.WriteLineAsync("META_CAPI_SMOKE=BLOCKED_CONFIGURATION_MISSING");
            return 64;
        }

        if (string.IsNullOrWhiteSpace(options.TestEventCode))
        {
            await error.WriteLineAsync("META_CAPI_SMOKE=BLOCKED_TEST_EVENT_CODE_MISSING");
            return 64;
        }

        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var service = new MetaConversionsService(
            new SmokeHttpClientFactory(client),
            new SmokeEventStore(),
            options,
            NullLogger<MetaConversionsService>.Instance);

        return await RunAsync(service, options, output, error, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        MetaConversionsService service,
        MetaConversionsOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.TestEventCode))
        {
            await error.WriteLineAsync("META_CAPI_SMOKE=BLOCKED_TEST_EVENT_CODE_MISSING");
            return 64;
        }

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        string runId = $"{timestamp}_{Guid.NewGuid():N}"[..32];
        IReadOnlyList<MetaConversionEvent> events = BuildEvents(runId, DateTimeOffset.UtcNow, options);

        foreach (MetaConversionEvent conversionEvent in events)
        {
            MetaDeliveryResult result = await service.SendAsync(conversionEvent, cancellationToken);
            await output.WriteLineAsync(
                $"META_CAPI_SMOKE_EVENT={conversionEvent.EventName} EVENT_ID={conversionEvent.EventId} STATUS={result.Status}");
            if (result.Status != MetaDeliveryStatus.Sent)
            {
                await error.WriteLineAsync($"META_CAPI_SMOKE=FAILED EVENT={conversionEvent.EventName}");
                return 1;
            }
        }

        MetaConversionEvent purchase = events.Single(item => item.EventName == "Purchase");
        MetaDeliveryResult duplicate = await service.SendAsync(purchase, cancellationToken);
        await output.WriteLineAsync(
            $"META_CAPI_SMOKE_DUPLICATE=Purchase EVENT_ID={purchase.EventId} STATUS={duplicate.Status}");
        if (duplicate.Status != MetaDeliveryStatus.Duplicate)
        {
            await error.WriteLineAsync("META_CAPI_SMOKE=FAILED PURCHASE_DUPLICATE_NOT_BLOCKED");
            return 1;
        }

        await output.WriteLineAsync("META_CAPI_SMOKE=PASS");
        return 0;
    }

    internal static IReadOnlyList<MetaConversionEvent> BuildEvents(
        string runId,
        DateTimeOffset occurredAt,
        MetaConversionsOptions options)
    {
        string safeRunId = new string(runId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (safeRunId.Length == 0) throw new ArgumentException("Run ID inválido.", nameof(runId));

        var attribution = new MetaAttributionContext(
            Guid.NewGuid(),
            true,
            options.ConsentVersion,
            "203.0.113.10",
            "PremierHost Meta CAPI Synthetic Smoke Test",
            null,
            null,
            $"meta-smoke-{safeRunId}@example.invalid",
            "+12025550100",
            "Meta",
            "Smoke",
            $"smoke_external_{safeRunId}");

        string orderId = $"smoke_order_{safeRunId}";
        var checkoutData = new Dictionary<string, object?>
        {
            ["currency"] = "BRL",
            ["value"] = 10.00m,
            ["order_id"] = orderId,
            ["num_items"] = 1,
            ["content_name"] = "Plano Premier Host - Teste CAPI",
            ["content_type"] = "product",
            ["content_ids"] = new[] { "premier_host_smoke" },
            ["contents"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "premier_host_smoke",
                    ["quantity"] = 1,
                    ["item_price"] = 10.00m
                }
            }
        };

        MetaConversionEvent Event(
            string eventName,
            string eventId,
            string sourceUrl,
            IReadOnlyDictionary<string, object?> customData) =>
            new(eventName, eventId, sourceUrl, attribution, customData, occurredAt);

        return new[]
        {
            Event(
                "CompleteRegistration",
                $"smoke_complete_registration_{safeRunId}",
                "https://phost.pro/confirmar",
                new Dictionary<string, object?> { ["content_name"] = "Cadastro Premier Host - Teste CAPI" }),
            Event(
                "Lead",
                $"smoke_lead_{safeRunId}",
                "https://phost.pro/painel",
                new Dictionary<string, object?> { ["content_name"] = "Lead Premier Host - Teste CAPI" }),
            Event(
                "StartTrial",
                $"smoke_start_trial_{safeRunId}",
                "https://phost.pro/painel",
                new Dictionary<string, object?> { ["content_name"] = "Teste gratuito Premier Host - Teste CAPI" }),
            Event(
                "InitiateCheckout",
                $"smoke_initiate_checkout_{safeRunId}",
                "https://phost.pro/painel",
                checkoutData),
            Event(
                "Purchase",
                $"smoke_purchase_{safeRunId}",
                "https://phost.pro/painel",
                new Dictionary<string, object?>(checkoutData))
        };
    }

    private sealed class SmokeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SmokeHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class SmokeEventStore : IMetaEventStore
    {
        private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

        public Task<bool> TryBeginAsync(
            string eventId,
            string eventName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_reserved.Add(eventId));

        public Task MarkSucceededAsync(string eventId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkFailedAsync(string eventId, CancellationToken cancellationToken)
        {
            _reserved.Remove(eventId);
            return Task.CompletedTask;
        }
    }
}
