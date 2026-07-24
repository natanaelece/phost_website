using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PremierAPI.Services;

public sealed class MetaConversionsService
{
    private const int MaximumGraphErrorTextLength = 300;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex EmailPattern = new(
        @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Pattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv6Pattern = new(
        @"(?i)(?<![A-Z0-9:])[0-9A-F]{1,4}(?::[0-9A-F]{0,4}){2,}(?![A-Z0-9:])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MetaBrowserCookiePattern = new(
        @"(?i)\bfb\.[0-9]+\.[0-9]+\.[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenAssignmentPattern = new(
        @"(?i)\b(?:access[_-]?token|token|authorization)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhonePattern = new(
        @"(?<!\d)(?:\+?\d[\d\s().-]{7,}\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMetaEventStore _eventStore;
    private readonly MetaConversionsOptions _options;
    private readonly ILogger<MetaConversionsService> _logger;

    public MetaConversionsService(
        IHttpClientFactory httpClientFactory,
        IMetaEventStore eventStore,
        MetaConversionsOptions options,
        ILogger<MetaConversionsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _eventStore = eventStore;
        _options = options;
        _logger = logger;
    }

    public async Task<MetaDeliveryResult> SendAsync(
        MetaConversionEvent conversionEvent,
        CancellationToken cancellationToken = default)
    {
        if (!conversionEvent.Attribution.MarketingConsentGranted)
            return new MetaDeliveryResult(MetaDeliveryStatus.SkippedWithoutConsent);
        if (!_options.IsConfigured)
            return new MetaDeliveryResult(MetaDeliveryStatus.Disabled);

        bool acquired;
        try
        {
            acquired = await _eventStore.TryBeginAsync(
                conversionEvent.EventId,
                conversionEvent.EventName,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META CAPI] Falha ao reservar o evento {EventName} ({EventId}) para envio.",
                conversionEvent.EventName,
                conversionEvent.EventId);
            return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
        }

        if (!acquired)
            return new MetaDeliveryResult(MetaDeliveryStatus.Duplicate);

        try
        {
            var requestPayload = BuildRequestPayload(conversionEvent, _options.TestEventCode);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://graph.facebook.com/{_options.GraphApiVersion}/{_options.DatasetId}/events");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestPayload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.Timeout);
            using var response = await _httpClientFactory
                .CreateClient("MetaConversions")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                await _eventStore.MarkFailedAsync(conversionEvent.EventId, CancellationToken.None);
                MetaGraphErrorDiagnostic diagnostic =
                    await ReadGraphErrorDiagnosticAsync(response, cancellationToken);
                LogRejectedResponse(conversionEvent, response, diagnostic);
                return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
            }

            await _eventStore.MarkSucceededAsync(conversionEvent.EventId, CancellationToken.None);
            return new MetaDeliveryResult(MetaDeliveryStatus.Sent);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await TryMarkFailedAsync(conversionEvent.EventId);
            _logger.LogError(
                ex,
                "[META CAPI] Timeout ao enviar o evento {EventName} ({EventId}).",
                conversionEvent.EventName,
                conversionEvent.EventId);
            return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
        }
        catch (Exception ex)
        {
            await TryMarkFailedAsync(conversionEvent.EventId);
            _logger.LogError(
                ex,
                "[META CAPI] Falha ao enviar o evento {EventName} ({EventId}).",
                conversionEvent.EventName,
                conversionEvent.EventId);
            return new MetaDeliveryResult(MetaDeliveryStatus.Failed);
        }
    }

    internal static Dictionary<string, object?> BuildRequestPayload(
        MetaConversionEvent conversionEvent,
        string? testEventCode)
    {
        var eventData = new Dictionary<string, object?>
        {
            ["event_name"] = conversionEvent.EventName,
            ["event_time"] = (conversionEvent.OccurredAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            ["event_id"] = conversionEvent.EventId,
            ["action_source"] = "website",
            ["event_source_url"] = CanonicalizeSourceUrl(conversionEvent.EventSourceUrl),
            ["user_data"] = BuildUserData(conversionEvent.Attribution)
        };

        var customData = RemoveEmptyValues(conversionEvent.CustomData);
        if (customData.Count > 0)
            eventData["custom_data"] = customData;

        var payload = new Dictionary<string, object?>
        {
            ["data"] = new[] { eventData }
        };
        if (!string.IsNullOrWhiteSpace(testEventCode))
            payload["test_event_code"] = testEventCode.Trim();
        return payload;
    }

    internal static Dictionary<string, object?> BuildUserData(MetaAttributionContext attribution)
    {
        var userData = new Dictionary<string, object?>();
        AddHashed(userData, "em", NormalizeEmail(attribution.Email));
        AddHashed(userData, "ph", NormalizePhone(attribution.Phone));
        AddHashed(userData, "fn", NormalizeName(attribution.FirstName));
        AddHashed(userData, "ln", NormalizeName(attribution.LastName));
        AddHashed(userData, "external_id", NormalizeExternalId(attribution.ExternalId));
        AddRaw(userData, "client_ip_address", attribution.ClientIpAddress);
        AddRaw(userData, "client_user_agent", attribution.ClientUserAgent);
        AddRaw(userData, "fbp", attribution.Fbp);
        AddRaw(userData, "fbc", attribution.Fbc);
        return userData;
    }

    internal static string? NormalizeEmail(string? value) =>
        NullIfWhiteSpace(value)?.ToLowerInvariant();

    internal static string? NormalizePhone(string? value)
    {
        string digits = new((value ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    internal static string? NormalizeName(string? value)
    {
        string? trimmed = NullIfWhiteSpace(value);
        if (trimmed == null) return null;

        string decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Length == 0 ? null : builder.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static string? NormalizeExternalId(string? value) =>
        NullIfWhiteSpace(value)?.ToLowerInvariant();

    internal static string Sha256(string normalizedValue)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string CanonicalizeSourceUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? source))
            return "https://phost.pro/";
        string path = string.IsNullOrWhiteSpace(source.AbsolutePath) ? "/" : source.AbsolutePath;
        return $"https://phost.pro{path}";
    }

    internal static string SanitizeGraphErrorText(string? value)
    {
        string sanitized = NullIfWhiteSpace(value) ?? "indisponível";
        sanitized = TokenAssignmentPattern.Replace(sanitized, "[token removido]");
        sanitized = MetaBrowserCookiePattern.Replace(sanitized, "[cookie Meta removido]");
        sanitized = EmailPattern.Replace(sanitized, "[e-mail removido]");
        sanitized = Ipv4Pattern.Replace(sanitized, "[IP removido]");
        sanitized = Ipv6Pattern.Replace(sanitized, "[IP removido]");
        sanitized = PhonePattern.Replace(sanitized, "[telefone removido]");
        return sanitized.Length <= MaximumGraphErrorTextLength
            ? sanitized
            : sanitized[..(MaximumGraphErrorTextLength - 3)] + "...";
    }

    private async Task<MetaGraphErrorDiagnostic> ReadGraphErrorDiagnosticAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "desconhecido";
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        int responseLength = Encoding.UTF8.GetByteCount(body);
        body = body.Replace(_options.AccessToken, "[token removido]", StringComparison.Ordinal);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return MetaGraphErrorDiagnostic.NonJson(contentType, responseLength, body);
            }

            return new MetaGraphErrorDiagnostic(
                true,
                contentType,
                responseLength,
                ReadSanitizedString(error, "type"),
                ReadInteger(error, "code"),
                ReadInteger(error, "error_subcode"),
                ReadSanitizedString(error, "message"),
                ReadSanitizedString(error, "fbtrace_id"),
                null);
        }
        catch (JsonException)
        {
            return MetaGraphErrorDiagnostic.NonJson(contentType, responseLength, body);
        }
    }

    private void LogRejectedResponse(
        MetaConversionEvent conversionEvent,
        HttpResponseMessage response,
        MetaGraphErrorDiagnostic diagnostic)
    {
        if (diagnostic.IsGraphJson)
        {
            _logger.LogError(
                "[META CAPI] A Meta rejeitou o evento {EventName} ({EventId}) com HTTP {StatusCode}. " +
                "Type {GraphErrorType}; Code {GraphErrorCode}; Subcode {GraphErrorSubcode}; " +
                "Message {GraphErrorMessage}; Fbtrace {GraphFbtraceId}.",
                conversionEvent.EventName,
                conversionEvent.EventId,
                (int)response.StatusCode,
                diagnostic.Type ?? "indisponível",
                diagnostic.Code?.ToString(CultureInfo.InvariantCulture) ?? "indisponível",
                diagnostic.Subcode?.ToString(CultureInfo.InvariantCulture) ?? "indisponível",
                diagnostic.Message ?? "indisponível",
                diagnostic.FbtraceId ?? "indisponível");
            return;
        }

        _logger.LogError(
            "[META CAPI] A Meta rejeitou o evento {EventName} ({EventId}) com HTTP {StatusCode}. " +
            "ContentType {ContentType}; ResponseLength {ResponseLength}; ResponseText {ResponseText}.",
            conversionEvent.EventName,
            conversionEvent.EventId,
            (int)response.StatusCode,
            diagnostic.ContentType,
            diagnostic.ResponseLength,
            diagnostic.FallbackText ?? "indisponível");
    }

    private static string? ReadSanitizedString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? SanitizeGraphErrorText(value.GetString())
            : null;

    private static int? ReadInteger(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out JsonElement value)
        && value.TryGetInt32(out int integer)
            ? integer
            : null;

    private static Dictionary<string, object?> RemoveEmptyValues(
        IReadOnlyDictionary<string, object?>? values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (values == null) return result;

        foreach (var pair in values)
        {
            if (pair.Value == null) continue;
            if (pair.Value is string text && string.IsNullOrWhiteSpace(text)) continue;
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static void AddHashed(
        IDictionary<string, object?> userData,
        string key,
        string? normalizedValue)
    {
        if (normalizedValue != null)
            userData[key] = new[] { Sha256(normalizedValue) };
    }

    private static void AddRaw(
        IDictionary<string, object?> userData,
        string key,
        string? value)
    {
        string? normalized = NullIfWhiteSpace(value);
        if (normalized != null)
            userData[key] = normalized;
    }

    private async Task TryMarkFailedAsync(string eventId)
    {
        try
        {
            await _eventStore.MarkFailedAsync(eventId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META CAPI] Falha ao registrar o erro de entrega do evento {EventId}.",
                eventId);
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record MetaGraphErrorDiagnostic(
        bool IsGraphJson,
        string ContentType,
        int ResponseLength,
        string? Type,
        int? Code,
        int? Subcode,
        string? Message,
        string? FbtraceId,
        string? FallbackText)
    {
        public static MetaGraphErrorDiagnostic NonJson(
            string contentType,
            int responseLength,
            string body) =>
            new(
                false,
                contentType,
                responseLength,
                null,
                null,
                null,
                null,
                null,
                SanitizeGraphErrorText(body));
    }
}
