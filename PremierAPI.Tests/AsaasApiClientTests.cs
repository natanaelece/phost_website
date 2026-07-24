using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class AsaasApiClientTests
{
    [Fact]
    public async Task StaticPixGenerationSuccessPreservesEndpointPayloadAndBackendHeader()
    {
        var handler = new SequenceHandler(Response(
            HttpStatusCode.OK,
            """{"id":"qr_synthetic","encodedImage":"synthetic-image","payload":"synthetic-pix"}"""));
        AsaasApiClient client = Client(handler);
        var payload = new AsaasStaticPixRequest(
            "synthetic-address-key",
            "Licença (mensal) - AnyDesk: 123456789",
            105m,
            "ALL",
            900,
            false,
            "synthetic-external-reference");

        AsaasApiOperationResult<AsaasStaticPixQrCode> result =
            await client.CreateStaticPixQrCodeAsync(payload);

        Assert.True(result.IsSuccess);
        Assert.Equal("qr_synthetic", result.Value?.Id);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.example.test/v3/pix/qrCodes/static",
            request.Uri);
        Assert.Equal("synthetic-production-key", request.AccessToken);
        Assert.Equal(AsaasHttpClientNames.UserAgent, request.UserAgent);
        Assert.Contains("\"expirationSeconds\":900", request.Body);
        Assert.Contains("\"allowsMultiplePayments\":false", request.Body);
        Assert.DoesNotContain("synthetic-production-key", request.Uri);
    }

    [Fact]
    public async Task StaticPixGenerationRejectedDoesNotExposeResponse()
    {
        const string sensitive = "person@example.test access_token=secret-token";
        var handler = new SequenceHandler(Response(
            HttpStatusCode.BadRequest,
            $$"""{"errors":[{"code":"invalid_value","description":"{{sensitive}}"}]}"""));
        var logger = new RecordingLogger<AsaasApiClient>();
        AsaasApiClient client = Client(handler, logger);

        AsaasApiOperationResult<AsaasStaticPixQrCode> result =
            await client.CreateStaticPixQrCodeAsync(SyntheticPixRequest());

        Assert.False(result.IsSuccess);
        string log = Assert.Single(logger.ErrorMessages);
        Assert.Contains("invalid_value", log);
        Assert.DoesNotContain("person@example.test", log);
        Assert.DoesNotContain("secret-token", log);
    }

    [Fact]
    public async Task ActivePixKeyListingRejectedReturnsFailureWithoutThrowing()
    {
        var handler = new SequenceHandler(Response(
            HttpStatusCode.Forbidden,
            """{"errors":[{"code":"access_denied","description":"Acesso negado"}]}"""));
        AsaasApiClient client = Client(handler);

        AsaasApiOperationResult<string> result =
            await client.GetActivePixAddressKeyAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(
            "https://api.example.test/v3/pix/addressKeys?status=ACTIVE&limit=100",
            Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task CancellationSuccessAndNotFoundFollowCurrentAcceptedBehavior()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.NoContent, string.Empty),
            Response(HttpStatusCode.NotFound, """{"errors":[]}"""));
        AsaasApiClient client = Client(handler);

        AsaasApiOperationResult success = await client.CancelStaticPixQrCodeAsync(
            "qr_cancel_success",
            acceptNotFound: true);
        AsaasApiOperationResult missing = await client.CancelStaticPixQrCodeAsync(
            "qr_already_missing",
            acceptNotFound: true);

        Assert.True(success.IsSuccess);
        Assert.True(missing.IsSuccess);
        Assert.True(missing.IsNotFound);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Delete, request.Method));
    }

    [Fact]
    public async Task CustomerSyncListsAndBatchDisablesEveryNotification()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.OK, "{}"),
            Response(
                HttpStatusCode.OK,
                """{"data":[{"id":"not_one"},{"id":"not_two"}]}"""),
            Response(HttpStatusCode.OK, "{}"));
        AsaasApiClient client = Client(handler);

        await client.SyncCustomerAndDisableNotificationsAsync(
            "cus_synthetic",
            Guid.Parse("550e8400-e29b-41d4-a716-446655440000"),
            "Synthetic Customer",
            "synthetic@example.test",
            "+55 (34) 99918-7189",
            useSandbox: true);

        Assert.Collection(
            handler.Requests,
            update =>
            {
                Assert.Equal(HttpMethod.Put, update.Method);
                Assert.Equal(
                    "https://sandbox.example.test/v3/customers/cus_synthetic",
                    update.Uri);
                Assert.Contains("\"notificationDisabled\":true", update.Body);
                Assert.Equal("synthetic-sandbox-key", update.AccessToken);
            },
            list =>
            {
                Assert.Equal(HttpMethod.Get, list.Method);
                Assert.EndsWith(
                    "/customers/cus_synthetic/notifications",
                    list.Uri,
                    StringComparison.Ordinal);
            },
            batch =>
            {
                Assert.Equal(HttpMethod.Put, batch.Method);
                Assert.EndsWith(
                    "/notifications/batch",
                    batch.Uri,
                    StringComparison.Ordinal);
                Assert.Contains("\"id\":\"not_one\"", batch.Body);
                Assert.Contains("\"id\":\"not_two\"", batch.Body);
                Assert.Contains("\"enabled\":false", batch.Body);
                Assert.Contains("\"whatsappEnabledForCustomer\":false", batch.Body);
            });
    }

    [Fact]
    public async Task CustomerUpdateHttpFailureRemainsBestEffortAndDoesNotLeakBody()
    {
        const string sensitive =
            "person@example.test cus_customer_secret access_token=secret-token";
        var handler = new SequenceHandler(
            Response(
                HttpStatusCode.BadRequest,
                $$"""{"errors":[{"code":"invalid_customer","description":"{{sensitive}}"}]}"""),
            Response(HttpStatusCode.OK, """{"data":[{"id":"not_one"}]}"""),
            Response(HttpStatusCode.OK, "{}"));
        var logger = new RecordingLogger<AsaasApiClient>();
        AsaasApiClient client = Client(handler, logger);

        Exception? exception = await Record.ExceptionAsync(() =>
            client.SyncCustomerAndDisableNotificationsAsync(
                "cus_customer_secret",
                Guid.NewGuid(),
                "Customer Name",
                "person@example.test",
                "+5511999999999",
                useSandbox: false));

        Assert.Null(exception);
        Assert.Equal(3, handler.Requests.Count);
        string error = Assert.Single(logger.ErrorMessages);
        Assert.Contains("invalid_customer", error);
        Assert.DoesNotContain("person@example.test", error);
        Assert.DoesNotContain("cus_customer_secret", error);
        Assert.DoesNotContain("secret-token", error);
    }

    [Fact]
    public async Task TransportFailureDoesNotBreakBestEffortCustomerSync()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException(
                "synthetic failure https://api.example.test/v3/customers/cus_secret"));
        var logger = new RecordingLogger<AsaasApiClient>();
        AsaasApiClient client = Client(handler, logger);

        Exception? exception = await Record.ExceptionAsync(() =>
            client.SyncCustomerAndDisableNotificationsAsync(
                "cus_secret",
                Guid.NewGuid(),
                "Synthetic",
                "synthetic@example.test",
                "+5511999999999",
                useSandbox: false));

        Assert.Null(exception);
        Assert.NotEmpty(logger.ErrorMessages);
        Assert.All(
            logger.ErrorMessages,
            message => Assert.DoesNotContain("cus_secret", message));
    }

    private static AsaasStaticPixRequest SyntheticPixRequest() =>
        new(
            "synthetic-address-key",
            "Licença (mensal) - AnyDesk: 123456789",
            105m,
            "ALL",
            900,
            false,
            "synthetic-reference");

    private static AsaasApiClient Client(
        HttpMessageHandler handler,
        RecordingLogger<AsaasApiClient>? logger = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asaas:UseSandbox"] = "false"
            })
            .Build();
        var factory = new SyntheticClientFactory(handler);
        var provider = new AsaasHttpClientProvider(factory, configuration);
        return new AsaasApiClient(
            provider,
            logger ?? new RecordingLogger<AsaasApiClient>());
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class SyntheticClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SyntheticClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            bool sandbox = name == AsaasHttpClientNames.Sandbox;
            var client = new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(
                    sandbox
                        ? "https://sandbox.example.test/v3/"
                        : "https://api.example.test/v3/")
            };
            client.DefaultRequestHeaders.Add(
                "access_token",
                sandbox ? "synthetic-sandbox-key" : "synthetic-production-key");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AsaasHttpClientNames.UserAgent);
            return client;
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("access_token", out IEnumerable<string>? tokens)
                    ? tokens.Single()
                    : string.Empty,
                request.Headers.UserAgent.ToString()));
            return _responses.Dequeue();
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string AccessToken,
        string UserAgent);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> ErrorMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                ErrorMessages.Add(formatter(state, exception));
        }
    }
}
