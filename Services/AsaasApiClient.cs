using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PremierAPI.Services;

public sealed record AsaasStaticPixRequest(
    [property: JsonPropertyName("addressKey")] string AddressKey,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("expirationSeconds")] int ExpirationSeconds,
    [property: JsonPropertyName("allowsMultiplePayments")] bool AllowsMultiplePayments,
    [property: JsonPropertyName("externalReference")] string ExternalReference);

public sealed record AsaasStaticPixQrCode(
    string Id,
    string EncodedImage,
    string Payload);

public sealed record AsaasPixQrCode(
    string EncodedImage,
    string Payload);

public sealed record AsaasApiOperationResult(
    bool IsSuccess,
    HttpStatusCode StatusCode)
{
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
    public bool IsSuccessOrNotFound => IsSuccess || IsNotFound;
}

public sealed record AsaasApiOperationResult<T>(
    bool IsSuccess,
    HttpStatusCode StatusCode,
    T? Value,
    bool IsInvalidResponse = false)
{
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}

public sealed class AsaasApiClient
{
    private readonly AsaasHttpClientProvider _clientProvider;
    private readonly ILogger<AsaasApiClient> _logger;

    public AsaasApiClient(
        AsaasHttpClientProvider clientProvider,
        ILogger<AsaasApiClient> logger)
    {
        _clientProvider = clientProvider;
        _logger = logger;
    }

    public async Task<AsaasApiOperationResult<string>> GetActivePixAddressKeyAsync(
        bool? useSandbox = null,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            "pix/addressKeys?status=ACTIVE&limit=100",
            "listar chaves Pix ativas",
            useSandbox,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogRejectedResponseAsync(
                response,
                "listar chaves Pix ativas",
                cancellationToken);
            return new(false, response.StatusCode, null);
        }

        try
        {
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseJson);
            foreach (JsonElement addressKey in document.RootElement
                .GetProperty("data")
                .EnumerateArray())
            {
                if (addressKey.TryGetProperty("key", out JsonElement keyElement))
                {
                    string? key = keyElement.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                        return new(true, response.StatusCode, key);
                }
            }

            return new(true, response.StatusCode, string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Resposta inválida ao listar chaves Pix ativas. HTTP {StatusCode}.",
                (int)response.StatusCode);
            return new(false, response.StatusCode, null);
        }
    }

    public async Task<AsaasApiOperationResult<AsaasStaticPixQrCode>> CreateStaticPixQrCodeAsync(
        AsaasStaticPixRequest payload,
        bool? useSandbox = null,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            HttpMethod.Post,
            "pix/qrCodes/static",
            payload,
            "gerar QR Code Pix estático",
            useSandbox,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogRejectedResponseAsync(
                response,
                "gerar QR Code Pix estático",
                cancellationToken);
            return new(false, response.StatusCode, null);
        }

        try
        {
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseJson);
            string id = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
            string encodedImage =
                document.RootElement.GetProperty("encodedImage").GetString() ?? string.Empty;
            string pixPayload =
                document.RootElement.GetProperty("payload").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(encodedImage) ||
                string.IsNullOrWhiteSpace(pixPayload))
            {
                _logger.LogError(
                    "[ASAAS] Resposta incompleta ao gerar QR Code Pix estático. HTTP {StatusCode}; " +
                    "QrCode {QrCodeReference}.",
                    (int)response.StatusCode,
                    AsaasErrorSanitizer.SafeIdentifier(id));
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _ = await CancelStaticPixQrCodeAsync(
                        id,
                        acceptNotFound: true,
                        useSandbox,
                        CancellationToken.None);
                }
                return new(false, response.StatusCode, null, IsInvalidResponse: true);
            }

            return new(
                true,
                response.StatusCode,
                new AsaasStaticPixQrCode(id, encodedImage, pixPayload));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Resposta inválida ao gerar QR Code Pix estático. HTTP {StatusCode}.",
                (int)response.StatusCode);
            return new(false, response.StatusCode, null, IsInvalidResponse: true);
        }
    }

    public async Task<AsaasApiOperationResult<AsaasPixQrCode>> GetPaymentPixQrCodeAsync(
        string paymentId,
        bool? useSandbox = null,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(paymentId)}/pixQrCode",
            "consultar QR Code Pix de cobrança",
            useSandbox,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogRejectedResponseAsync(
                response,
                "consultar QR Code Pix de cobrança",
                cancellationToken);
            return new(false, response.StatusCode, null);
        }

        try
        {
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseJson);
            return new(
                true,
                response.StatusCode,
                new AsaasPixQrCode(
                    document.RootElement.GetProperty("encodedImage").GetString() ?? string.Empty,
                    document.RootElement.GetProperty("payload").GetString() ?? string.Empty));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Resposta inválida ao consultar QR Code Pix de cobrança. HTTP {StatusCode}; " +
                "Payment {PaymentReference}.",
                (int)response.StatusCode,
                AsaasErrorSanitizer.SafeIdentifier(paymentId));
            return new(false, response.StatusCode, null);
        }
    }

    public Task<AsaasApiOperationResult> CancelStaticPixQrCodeAsync(
        string qrCodeId,
        bool acceptNotFound,
        bool? useSandbox = null,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            $"pix/qrCodes/static/{Uri.EscapeDataString(qrCodeId)}",
            "cancelar QR Code Pix estático",
            acceptNotFound,
            useSandbox,
            cancellationToken);

    public Task<AsaasApiOperationResult> CancelPaymentAsync(
        string paymentId,
        bool acceptNotFound,
        bool? useSandbox = null,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            $"payments/{Uri.EscapeDataString(paymentId)}",
            "cancelar cobrança Pix",
            acceptNotFound,
            useSandbox,
            cancellationToken);

    public async Task<AsaasApiOperationResult> RefundPaymentAsync(
        string paymentId,
        bool? useSandbox = null,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            $"payments/{Uri.EscapeDataString(paymentId)}/refund",
            "reembolsar cobrança",
            useSandbox,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogRejectedResponseAsync(response, "reembolsar cobrança", cancellationToken);
            return new(false, response.StatusCode);
        }
        return new(true, response.StatusCode);
    }

    public async Task SyncCustomerAndDisableNotificationsAsync(
        string customerId,
        Guid userId,
        string name,
        string email,
        string whatsapp,
        bool useSandbox,
        CancellationToken cancellationToken = default)
    {
        string safeCustomerId = AsaasErrorSanitizer.SafeIdentifier(customerId);
        try
        {
            string cleanPhone = new((whatsapp ?? string.Empty).Where(char.IsDigit).ToArray());
            var customerUpdate = new
            {
                name,
                email,
                mobilePhone = cleanPhone,
                externalReference = userId.ToString(),
                groupName = "PremierHost",
                notificationDisabled = true
            };
            using HttpResponseMessage customerResponse = await SendJsonAsync(
                HttpMethod.Put,
                $"customers/{Uri.EscapeDataString(customerId)}",
                customerUpdate,
                "atualizar cliente",
                useSandbox,
                cancellationToken);
            bool customerUpdated = customerResponse.IsSuccessStatusCode;
            if (!customerUpdated)
            {
                await LogRejectedResponseAsync(
                    customerResponse,
                    "atualizar cliente",
                    cancellationToken);
            }

            using HttpResponseMessage notificationsResponse = await SendAsync(
                HttpMethod.Get,
                $"customers/{Uri.EscapeDataString(customerId)}/notifications",
                "listar notificações do cliente",
                useSandbox,
                cancellationToken: cancellationToken);
            if (!notificationsResponse.IsSuccessStatusCode)
            {
                await LogRejectedResponseAsync(
                    notificationsResponse,
                    "listar notificações do cliente",
                    cancellationToken);
                return;
            }

            List<Dictionary<string, object>> notificationUpdates;
            try
            {
                string notificationsJson =
                    await notificationsResponse.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument notificationsDocument =
                    JsonDocument.Parse(notificationsJson);
                notificationUpdates = notificationsDocument.RootElement
                    .GetProperty("data")
                    .EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out _))
                    .Select(item => new Dictionary<string, object>
                    {
                        ["id"] = item.GetProperty("id").GetString() ?? string.Empty,
                        ["enabled"] = false,
                        ["emailEnabledForProvider"] = false,
                        ["smsEnabledForProvider"] = false,
                        ["emailEnabledForCustomer"] = false,
                        ["smsEnabledForCustomer"] = false,
                        ["phoneCallEnabledForCustomer"] = false,
                        ["whatsappEnabledForCustomer"] = false
                    })
                    .Where(item => !string.IsNullOrWhiteSpace((string)item["id"]))
                    .ToList();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                _logger.LogError(
                    AsaasErrorSanitizer.SafeException(ex),
                    "[ASAAS] Resposta inválida ao listar notificações do cliente {CustomerReference}.",
                    safeCustomerId);
                return;
            }

            if (notificationUpdates.Count == 0)
            {
                _logger.LogWarning(
                    "[ASAAS] Nenhuma notificação encontrada para o cliente {CustomerReference}.",
                    safeCustomerId);
                return;
            }

            var batchPayload = new
            {
                customer = customerId,
                notifications = notificationUpdates
            };
            using HttpResponseMessage batchResponse = await SendJsonAsync(
                HttpMethod.Put,
                "notifications/batch",
                batchPayload,
                "desativar notificações do cliente",
                useSandbox,
                cancellationToken);
            if (!batchResponse.IsSuccessStatusCode)
            {
                await LogRejectedResponseAsync(
                    batchResponse,
                    "desativar notificações do cliente",
                    cancellationToken);
                return;
            }

            if (customerUpdated)
            {
                _logger.LogInformation(
                    "[ASAAS] Cliente {CustomerReference} atualizado e com {Count} notificações desativadas.",
                    safeCustomerId,
                    notificationUpdates.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Falha ao sincronizar cliente {CustomerReference} e desativar notificações.",
                safeCustomerId);
        }
    }

    private async Task<AsaasApiOperationResult> DeleteAsync(
        string relativeUri,
        string operation,
        bool acceptNotFound,
        bool? useSandbox,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            relativeUri,
            operation,
            useSandbox,
            cancellationToken: cancellationToken);
        bool accepted = response.IsSuccessStatusCode ||
            (acceptNotFound && response.StatusCode == HttpStatusCode.NotFound);
        if (!accepted)
            await LogRejectedResponseAsync(response, operation, cancellationToken);
        return new(accepted, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string relativeUri,
        object payload,
        string operation,
        bool? useSandbox,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        return await SendAsync(
            method,
            relativeUri,
            operation,
            useSandbox,
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        string operation,
        bool? useSandbox,
        HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = useSandbox.HasValue
                ? _clientProvider.CreateClient(useSandbox.Value)
                : _clientProvider.CreateCurrentClient();
            using var request = new HttpRequestMessage(method, relativeUri)
            {
                Content = content
            };
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            content?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            content?.Dispose();
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Falha de transporte ao {Operation}.",
                operation);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"errors\":[{\"code\":\"transport_error\",\"description\":\"Falha de transporte sanitizada.\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private async Task LogRejectedResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            AsaasErrorDiagnostic diagnostic =
                await AsaasErrorSanitizer.ReadAsync(response, cancellationToken);
            _logger.LogError(
                "[ASAAS] Operação {Operation} rejeitada. HTTP {StatusCode}; ContentType {ContentType}; " +
                "ResponseLength {ResponseLength}; ErrorCount {ErrorCount}; Codes {ErrorCodes}; " +
                "Description {Description}; Correlation {CorrelationId}.",
                operation,
                (int)diagnostic.StatusCode,
                diagnostic.ContentType,
                diagnostic.ResponseLength,
                diagnostic.ErrorCount,
                diagnostic.ErrorCodes,
                diagnostic.Description,
                diagnostic.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                AsaasErrorSanitizer.SafeException(ex),
                "[ASAAS] Operação {Operation} rejeitada. HTTP {StatusCode}; diagnóstico da resposta indisponível.",
                operation,
                (int)response.StatusCode);
        }
    }
}
