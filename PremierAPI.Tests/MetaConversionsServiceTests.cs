using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class MetaConversionsServiceTests
{
    [Fact]
    public void ConsentVersion_InvalidatesPreviousReviewChoice()
    {
        Assert.Equal("2", MetaConversionsOptions.DefaultConsentVersion);
    }

    [Theory]
    [InlineData("  USER@Example.COM ", "user@example.com")]
    [InlineData("", null)]
    public void EmailNormalization_IsCanonical(string input, string? expected)
    {
        Assert.Equal(expected, MetaConversionsService.NormalizeEmail(input));
    }

    [Theory]
    [InlineData("+55 (34) 99918-7189", "5534999187189")]
    [InlineData("sem telefone", null)]
    public void PhoneNormalization_KeepsOnlyDigits(string input, string? expected)
    {
        Assert.Equal(expected, MetaConversionsService.NormalizePhone(input));
    }

    [Theory]
    [InlineData(" Natanael ", "natanael")]
    [InlineData("Élise-D'Ávila", "elisedavila")]
    public void NameNormalization_RemovesFormattingAndDiacritics(string input, string expected)
    {
        Assert.Equal(expected, MetaConversionsService.NormalizeName(input));
    }

    [Fact]
    public void ExternalIdNormalization_TrimsAndLowercases()
    {
        Assert.Equal(
            "d32afbf6-f90c-46f2-9d7c-f33a34808f25",
            MetaConversionsService.NormalizeExternalId(
                " D32AFBF6-F90C-46F2-9D7C-F33A34808F25 "));
    }

    [Fact]
    public void Sha256_UsesLowercaseHex()
    {
        Assert.Equal(
            "973dfe463ec85785f5f95af5ba3906eedb2d931c24e69824a89ea65dba4e813b",
            MetaConversionsService.Sha256("test@example.com"));
    }

    [Fact]
    public void EmptyFields_AreOmittedFromPayload()
    {
        Dictionary<string, object?> payload = MetaConversionsService.BuildRequestPayload(
            Event(consent: true, customData: new Dictionary<string, object?>
            {
                ["content_name"] = "",
                ["value"] = null
            }),
            null);

        var data = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(payload["data"])));
        var userData = Assert.IsType<Dictionary<string, object?>>(data["user_data"]);
        Assert.Empty(userData);
        Assert.False(data.ContainsKey("custom_data"));
    }

    [Fact]
    public void UserData_HashesIdentityButKeepsTechnicalFieldsRaw()
    {
        var attribution = new MetaAttributionContext(
            Guid.NewGuid(),
            true,
            "1",
            "203.0.113.10",
            "Browser Test",
            "fb.1.123.456",
            "fb.1.123.click",
            "USER@example.com",
            "+55 (34) 99918-7189",
            "Élise",
            "D'Ávila",
            "USER-ID");

        Dictionary<string, object?> userData =
            MetaConversionsService.BuildUserData(attribution);

        Assert.Equal("203.0.113.10", userData["client_ip_address"]);
        Assert.Equal("Browser Test", userData["client_user_agent"]);
        Assert.Equal("fb.1.123.456", userData["fbp"]);
        Assert.Equal("fb.1.123.click", userData["fbc"]);
        Assert.Equal(
            new[] { MetaConversionsService.Sha256("user@example.com") },
            Assert.IsType<string[]>(userData["em"]));
        Assert.Equal(
            new[] { MetaConversionsService.Sha256("user-id") },
            Assert.IsType<string[]>(userData["external_id"]));
    }

    [Fact]
    public void SourceUrl_IsCanonicalAndNeverUsesWwwOrQuery()
    {
        Assert.Equal(
            "https://phost.pro/guia-wyd",
            MetaConversionsService.CanonicalizeSourceUrl(
                "https://www.phost.pro/guia-wyd?fbclid=secret"));
    }

    [Fact]
    public void TestEventCode_IsOmittedWhenNotConfigured()
    {
        Dictionary<string, object?> payload =
            MetaConversionsService.BuildRequestPayload(Event(consent: true), null);

        Assert.False(payload.ContainsKey("test_event_code"));
    }

    [Fact]
    public void TestEventCode_IncludesTest30146WhenConfigured()
    {
        Dictionary<string, object?> payload =
            MetaConversionsService.BuildRequestPayload(Event(consent: true), "TEST30146");

        Assert.Equal("TEST30146", payload["test_event_code"]);
    }

    [Fact]
    public void EventIds_AreDeterministic()
    {
        Guid id = Guid.Parse("d32afbf6-f90c-46f2-9d7c-f33a34808f25");

        Assert.Equal("lead_d32afbf6f90c46f29d7cf33a34808f25", MetaBusinessEventService.LeadEventId(id));
        Assert.Equal("complete_registration_d32afbf6f90c46f29d7cf33a34808f25", MetaBusinessEventService.RegistrationEventId(id));
        Assert.Equal("start_trial_d32afbf6f90c46f29d7cf33a34808f25", MetaBusinessEventService.StartTrialEventId(id));
        Assert.Equal("initiate_checkout_d32afbf6f90c46f29d7cf33a34808f25", MetaBusinessEventService.InitiateCheckoutEventId(id));
        Assert.Equal("purchase_d32afbf6f90c46f29d7cf33a34808f25", MetaBusinessEventService.PurchaseEventId(id));
    }

    [Fact]
    public async Task Purchase_IsIdempotent()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        MetaConversionsService service = Service(handler, new InMemoryEventStore());
        MetaConversionEvent purchase = Event(
            consent: true,
            eventName: "Purchase",
            eventId: "purchase_order1");

        MetaDeliveryResult first = await service.SendAsync(purchase);
        MetaDeliveryResult second = await service.SendAsync(purchase);

        Assert.Equal(MetaDeliveryStatus.Sent, first.Status);
        Assert.Equal(MetaDeliveryStatus.Duplicate, second.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DuplicateWebhookPurchase_IsSentOnce()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        MetaConversionsService service = Service(handler, new InMemoryEventStore());
        MetaConversionEvent webhookAttempt = Event(
            consent: true,
            eventName: "Purchase",
            eventId: "purchase_f6f4e631d4ef47c0b202bf93fe64f25d");

        await service.SendAsync(webhookAttempt);
        await service.SendAsync(webhookAttempt);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConsentMissing_DoesNotCallCapi()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        MetaConversionsService service = Service(handler, new InMemoryEventStore());

        MetaDeliveryResult result = await service.SendAsync(Event(consent: false));

        Assert.Equal(MetaDeliveryStatus.SkippedWithoutConsent, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task MetaFailure_DoesNotThrowOrInterruptCommercialFlow()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        MetaConversionsService service = Service(handler, new InMemoryEventStore());
        bool commercialFlowCompleted = false;

        MetaDeliveryResult result = await service.SendAsync(Event(consent: true));
        commercialFlowCompleted = true;

        Assert.True(commercialFlowCompleted);
        Assert.Equal(MetaDeliveryStatus.Failed, result.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MetaTimeout_DoesNotThrowOrInterruptCommercialFlow()
    {
        MetaConversionsService service = Service(
            new ExceptionHandler(new TaskCanceledException("synthetic timeout")),
            new InMemoryEventStore());

        MetaDeliveryResult result = await service.SendAsync(Event(consent: true));

        Assert.Equal(MetaDeliveryStatus.Failed, result.Status);
    }

    [Fact]
    public async Task MetaTransportError_DoesNotThrowOrInterruptCommercialFlow()
    {
        MetaConversionsService service = Service(
            new ExceptionHandler(new HttpRequestException("synthetic transport error")),
            new InMemoryEventStore());

        MetaDeliveryResult result = await service.SendAsync(Event(consent: true));

        Assert.Equal(MetaDeliveryStatus.Failed, result.Status);
    }

    [Fact]
    public async Task HttpRequest_UsesBearerAndNeverPlacesTokenInUriOrBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        MetaConversionsService service = Service(handler, new InMemoryEventStore());

        await service.SendAsync(Event(consent: true));

        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.DoesNotContain("secret-test-token", handler.RequestUri ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("secret-test-token", handler.Body ?? "", StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("TEST30146", json.RootElement.GetProperty("test_event_code").GetString());
    }

    private static MetaConversionsService Service(
        HttpMessageHandler handler,
        IMetaEventStore store)
    {
        var client = new HttpClient(handler);
        var factory = new SingleClientFactory(client);
        var options = new MetaConversionsOptions
        {
            DatasetId = "123456789",
            AccessToken = "secret-test-token",
            GraphApiVersion = "v25.0",
            TestEventCode = "TEST30146",
            ConsentVersion = "1",
            Timeout = TimeSpan.FromSeconds(2)
        };
        return new MetaConversionsService(
            factory,
            store,
            options,
            NullLogger<MetaConversionsService>.Instance);
    }

    private sealed class ExceptionHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ExceptionHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    private static MetaConversionEvent Event(
        bool consent,
        string eventName = "Lead",
        string eventId = "lead_test",
        IReadOnlyDictionary<string, object?>? customData = null)
    {
        var attribution = new MetaAttributionContext(
            Guid.Parse("64a2de10-a47e-4573-9ac8-85cafb9029e2"),
            consent,
            "1",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        return new MetaConversionEvent(
            eventName,
            eventId,
            "https://www.phost.pro/painel?ignored=true",
            attribution,
            customData,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public int CallCount { get; private set; }
        public string? Body { get; private set; }
        public string? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class InMemoryEventStore : IMetaEventStore
    {
        private readonly HashSet<string> _sentOrProcessing = new(StringComparer.Ordinal);

        public Task<bool> TryBeginAsync(
            string eventId,
            string eventName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_sentOrProcessing.Add(eventId));

        public Task MarkSucceededAsync(string eventId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkFailedAsync(string eventId, CancellationToken cancellationToken)
        {
            _sentOrProcessing.Remove(eventId);
            return Task.CompletedTask;
        }
    }
}
