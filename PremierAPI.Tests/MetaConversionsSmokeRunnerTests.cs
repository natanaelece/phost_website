using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class MetaConversionsSmokeRunnerTests
{
    [Fact]
    public async Task SmokeRunner_IsBlockedWithoutTestEventCode()
    {
        var handler = new RecordingHandler();
        MetaConversionsOptions options = Options(testEventCode: null);
        MetaConversionsService service = Service(handler, options);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await MetaConversionsSmokeRunner.RunAsync(
            service,
            options,
            output,
            error);

        Assert.Equal(64, exitCode);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("BLOCKED_TEST_EVENT_CODE_MISSING", error.ToString());
    }

    [Fact]
    public async Task SmokeRunner_SendsFiveEventsAndDeduplicatesPurchase()
    {
        var handler = new RecordingHandler();
        MetaConversionsOptions options = Options("TEST30146");
        MetaConversionsService service = Service(handler, options);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await MetaConversionsSmokeRunner.RunAsync(
            service,
            options,
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, handler.CallCount);
        Assert.Empty(error.ToString());
        Assert.Contains("META_CAPI_SMOKE=PASS", output.ToString());
        Assert.Contains("STATUS=Duplicate", output.ToString());

        string[] expectedNames =
        {
            "CompleteRegistration",
            "Lead",
            "StartTrial",
            "InitiateCheckout",
            "Purchase"
        };
        Assert.Equal(expectedNames, handler.Bodies.Select(EventName));
        Assert.All(handler.Bodies, AssertPayloadQuality);

        using JsonDocument checkout = JsonDocument.Parse(handler.Bodies[3]);
        AssertCommerce(checkout.RootElement, "InitiateCheckout");
        using JsonDocument purchase = JsonDocument.Parse(handler.Bodies[4]);
        AssertCommerce(purchase.RootElement, "Purchase");
    }

    private static void AssertPayloadQuality(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("TEST30146", root.GetProperty("test_event_code").GetString());
        JsonElement data = root.GetProperty("data")[0];
        Assert.StartsWith("smoke_", data.GetProperty("event_id").GetString());
        Assert.True(data.GetProperty("event_time").GetInt64() > 0);
        Assert.Equal("website", data.GetProperty("action_source").GetString());
        Assert.StartsWith("https://phost.pro/", data.GetProperty("event_source_url").GetString());
        Assert.DoesNotContain("www.", data.GetProperty("event_source_url").GetString());

        JsonElement userData = data.GetProperty("user_data");
        AssertHashArray(userData, "em");
        AssertHashArray(userData, "ph");
        AssertHashArray(userData, "fn");
        AssertHashArray(userData, "ln");
        AssertHashArray(userData, "external_id");
        Assert.Equal("203.0.113.10", userData.GetProperty("client_ip_address").GetString());
        Assert.Equal(
            "PremierHost Meta CAPI Synthetic Smoke Test",
            userData.GetProperty("client_user_agent").GetString());
        Assert.False(userData.TryGetProperty("fbp", out _));
        Assert.False(userData.TryGetProperty("fbc", out _));
    }

    private static void AssertCommerce(JsonElement root, string eventName)
    {
        JsonElement data = root.GetProperty("data")[0];
        Assert.Equal(eventName, data.GetProperty("event_name").GetString());
        JsonElement customData = data.GetProperty("custom_data");
        Assert.Equal("BRL", customData.GetProperty("currency").GetString());
        Assert.Equal(10.00m, customData.GetProperty("value").GetDecimal());
        Assert.StartsWith("smoke_order_", customData.GetProperty("order_id").GetString());
        Assert.Equal(1, customData.GetProperty("num_items").GetInt32());
        Assert.Equal(
            "Plano Premier Host - Teste CAPI",
            customData.GetProperty("content_name").GetString());
        Assert.Equal("product", customData.GetProperty("content_type").GetString());
        Assert.Equal("premier_host_smoke", customData.GetProperty("content_ids")[0].GetString());
        Assert.Equal(
            customData.GetProperty("content_ids")[0].GetString(),
            customData.GetProperty("contents")[0].GetProperty("id").GetString());
    }

    private static void AssertHashArray(JsonElement userData, string property)
    {
        string? value = userData.GetProperty(property)[0].GetString();
        Assert.NotNull(value);
        Assert.Matches("^[a-f0-9]{64}$", value);
    }

    private static string EventName(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data")[0].GetProperty("event_name").GetString()!;
    }

    private static MetaConversionsService Service(
        RecordingHandler handler,
        MetaConversionsOptions options) =>
        new(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryEventStore(),
            options,
            NullLogger<MetaConversionsService>.Instance);

    private static MetaConversionsOptions Options(string? testEventCode) =>
        new()
        {
            DatasetId = "123456789",
            AccessToken = "synthetic-secret-token",
            GraphApiVersion = "v25.0",
            TestEventCode = testEventCode,
            ConsentVersion = "2",
            Timeout = TimeSpan.FromSeconds(2)
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public int CallCount => Bodies.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"events_received\":1}")
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class InMemoryEventStore : IMetaEventStore
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
