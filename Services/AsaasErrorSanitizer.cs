using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PremierAPI.Services;

public sealed record AsaasErrorDiagnostic(
    HttpStatusCode StatusCode,
    string ContentType,
    int ResponseLength,
    int ErrorCount,
    string ErrorCodes,
    string Description,
    string CorrelationId);

public static class AsaasErrorSanitizer
{
    public const int MaximumTextLength = 300;

    private static readonly Regex EmailPattern = new(
        @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Pattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv6Pattern = new(
        @"(?i)(?<![A-Z0-9:])[0-9A-F]{1,4}(?::[0-9A-F]{0,4}){2,}(?![A-Z0-9:])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CpfCnpjPattern = new(
        @"(?<!\d)(?:\d{3}[.\s-]?\d{3}[.\s-]?\d{3}-?\d{2}|\d{2}[.\s-]?\d{3}[.\s-]?\d{3}/?\d{4}-?\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhonePattern = new(
        @"(?<!\d)(?:\+?\d[\d\s().-]{7,}\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApiKeyPattern = new(
        @"(?i)(?:\$aact_[A-Z0-9_-]+|\b(?:api[_-]?key|access[_-]?token|webhook[_-]?token|authorization)\s*[""']?\s*[:=]\s*[""']?[^\s,""';|}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveLabelPattern = new(
        @"(?i)\b(?:name|nome|customer|cliente|mobilePhone|phone|telefone|email|address|endereco|endereço|street|rua|city|cidade|postalCode|cep|pixKey|chavePix|externalReference)\s*[""']?\s*[:=]\s*[""']?[^,""';|}\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveProsePattern = new(
        @"(?i)\b(?:name|nome|customer|cliente|address|endereco|endereço|street|rua)\s+(?:[^\s,;|.]+\s*){1,6}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExternalIdPattern = new(
        @"(?i)\b(?:cus|pay|pix|qrc|qr|not|evt)_[A-Z0-9_-]{4,}\b|\b[0-9A-F]{8}-[0-9A-F]{4}-[1-5][0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Base64Pattern = new(
        @"(?i)(?:data:image/[A-Z0-9.+-]+;base64,)?[A-Z0-9+/]{80,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PixPayloadPattern = new(
        @"(?<!\d)000201\d{40,}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<AsaasErrorDiagnostic> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        string contentType = Limit(
            response.Content.Headers.ContentType?.MediaType ?? "desconhecido");
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        int responseLength = Encoding.UTF8.GetByteCount(body);
        string correlationId = ReadCorrelationId(response);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            var errors = ReadErrors(document.RootElement);
            if (errors.Count > 0)
            {
                string codes = string.Join(
                    ",",
                    errors.Select(error => SanitizeCode(error.Code))
                        .Where(code => code.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Take(5));
                string description = SanitizeText(string.Join(
                    " | ",
                    errors.Select(error => error.Description)
                        .Where(value => !string.IsNullOrWhiteSpace(value))));
                if (correlationId == "indisponível")
                    correlationId = ReadJsonCorrelationId(document.RootElement);

                return new AsaasErrorDiagnostic(
                    response.StatusCode,
                    contentType,
                    responseLength,
                    errors.Count,
                    string.IsNullOrWhiteSpace(codes) ? "indisponível" : Limit(codes),
                    description,
                    correlationId);
            }
        }
        catch (JsonException)
        {
            // Respostas inválidas são tratadas como texto não JSON abaixo.
        }

        return new AsaasErrorDiagnostic(
            response.StatusCode,
            contentType,
            responseLength,
            0,
            "indisponível",
            SanitizeText(body),
            correlationId);
    }

    public static string SanitizeText(string? value)
    {
        string sanitized = string.IsNullOrWhiteSpace(value) ? "indisponível" : value;
        sanitized = HtmlTagPattern.Replace(sanitized, " ");
        sanitized = ApiKeyPattern.Replace(sanitized, "[credencial removida]");
        sanitized = SensitiveLabelPattern.Replace(sanitized, "[dado pessoal removido]");
        sanitized = SensitiveProsePattern.Replace(sanitized, "[dado pessoal removido]");
        sanitized = EmailPattern.Replace(sanitized, "[e-mail removido]");
        sanitized = Ipv4Pattern.Replace(sanitized, "[IP removido]");
        sanitized = Ipv6Pattern.Replace(sanitized, "[IP removido]");
        sanitized = CpfCnpjPattern.Replace(sanitized, "[documento removido]");
        sanitized = PhonePattern.Replace(sanitized, "[telefone removido]");
        sanitized = PixPayloadPattern.Replace(sanitized, "[payload Pix removido]");
        sanitized = Base64Pattern.Replace(sanitized, "[base64 removido]");
        sanitized = ExternalIdPattern.Replace(sanitized, "[id externo removido]");
        sanitized = WhitespacePattern.Replace(sanitized, " ").Trim();
        return Limit(string.IsNullOrWhiteSpace(sanitized) ? "indisponível" : sanitized);
    }

    public static string SafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "indisponível";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    public static Exception SafeException(Exception exception) =>
        new InvalidOperationException(
            $"Falha sanitizada na comunicação com o Asaas ({exception.GetType().Name}).");

    private static List<(string? Code, string? Description)> ReadErrors(JsonElement root)
    {
        var errors = new List<(string?, string?)>();
        if (root.ValueKind != JsonValueKind.Object)
            return errors;

        if (root.TryGetProperty("errors", out JsonElement errorsElement))
            AddErrorElements(errorsElement, errors);
        if (root.TryGetProperty("error", out JsonElement errorElement))
            AddErrorElements(errorElement, errors);

        if (errors.Count == 0 &&
            (root.TryGetProperty("code", out _) ||
             root.TryGetProperty("description", out _) ||
             root.TryGetProperty("message", out _)))
        {
            errors.Add((
                ReadString(root, "code"),
                ReadString(root, "description") ?? ReadString(root, "message")));
        }

        return errors;
    }

    private static void AddErrorElements(
        JsonElement element,
        ICollection<(string?, string?)> errors)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                AddErrorElements(item, errors);
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            errors.Add((
                ReadString(element, "code"),
                ReadString(element, "description") ?? ReadString(element, "message")));
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            errors.Add((null, element.GetString()));
        }
    }

    private static string? ReadString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadCorrelationId(HttpResponseMessage response)
    {
        string[] headerNames =
        {
            "asaas-request-id",
            "x-request-id",
            "request-id",
            "x-correlation-id",
            "trace-id"
        };
        foreach (string headerName in headerNames)
        {
            if (response.Headers.TryGetValues(headerName, out IEnumerable<string>? values))
                return SanitizeCorrelationId(values.FirstOrDefault());
        }
        return "indisponível";
    }

    private static string ReadJsonCorrelationId(JsonElement root)
    {
        foreach (string propertyName in new[] { "requestId", "correlationId", "traceId" })
        {
            string? value = ReadString(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return SanitizeCorrelationId(value);
        }
        return "indisponível";
    }

    private static string SanitizeCorrelationId(string? value)
    {
        string safe = new((value ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(100)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "indisponível" : safe;
    }

    private static string SanitizeCode(string? value)
    {
        string safe = new((value ?? string.Empty)
            .Where(character =>
                char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            .Take(64)
            .ToArray());
        return safe;
    }

    private static string Limit(string value) =>
        value.Length <= MaximumTextLength
            ? value
            : value[..(MaximumTextLength - 3)] + "...";
}
