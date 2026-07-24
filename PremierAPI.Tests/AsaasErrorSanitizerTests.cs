using System.Net;
using System.Text;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class AsaasErrorSanitizerTests
{
    [Fact]
    public async Task JsonErrorPreservesOnlySafeDiagnosticFields()
    {
        using HttpResponseMessage response = Response(
            HttpStatusCode.BadRequest,
            """
            {
              "errors": [
                {
                  "code": "invalid_action",
                  "description": "invalid request for person@example.test from 203.0.113.20"
                }
              ],
              "customer": "cus_customer_secret",
              "unknown": "must never be copied"
            }
            """);
        response.Headers.Add("x-request-id", "request-safe-123");

        AsaasErrorDiagnostic diagnostic = await AsaasErrorSanitizer.ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, diagnostic.StatusCode);
        Assert.Equal("application/json", diagnostic.ContentType);
        Assert.Equal(1, diagnostic.ErrorCount);
        Assert.Equal("invalid_action", diagnostic.ErrorCodes);
        Assert.Equal("request-safe-123", diagnostic.CorrelationId);
        Assert.DoesNotContain("person@example.test", diagnostic.Description);
        Assert.DoesNotContain("203.0.113.20", diagnostic.Description);
        Assert.DoesNotContain("cus_customer_secret", diagnostic.Description);
        Assert.DoesNotContain("must never be copied", diagnostic.Description);
    }

    [Fact]
    public async Task ErrorArrayCountsErrorsAndKeepsSafeCodes()
    {
        using HttpResponseMessage response = Response(
            HttpStatusCode.UnprocessableEntity,
            """
            [
              {"code":"ignored_root_array","description":"ignored"}
            ]
            """);
        using HttpResponseMessage normalResponse = Response(
            HttpStatusCode.UnprocessableEntity,
            """
            {"errors":[
              {"code":"invalid_value","description":"Primeiro erro"},
              {"code":"required_field","description":"Segundo erro"}
            ]}
            """);

        AsaasErrorDiagnostic malformedShape =
            await AsaasErrorSanitizer.ReadAsync(response);
        AsaasErrorDiagnostic diagnostic =
            await AsaasErrorSanitizer.ReadAsync(normalResponse);

        Assert.Equal(0, malformedShape.ErrorCount);
        Assert.Equal(2, diagnostic.ErrorCount);
        Assert.Contains("invalid_value", diagnostic.ErrorCodes);
        Assert.Contains("required_field", diagnostic.ErrorCodes);
        Assert.Contains("Primeiro erro", diagnostic.Description);
    }

    [Theory]
    [InlineData("{broken json person@example.test 198.51.100.4")]
    [InlineData("<html><body>Error for person@example.test at 198.51.100.4</body></html>")]
    [InlineData("plain error person@example.test 198.51.100.4")]
    public async Task NonJsonAndMalformedContentIsLimitedAndSanitized(string body)
    {
        using HttpResponseMessage response = Response(
            HttpStatusCode.BadGateway,
            body,
            "text/html");

        AsaasErrorDiagnostic diagnostic = await AsaasErrorSanitizer.ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadGateway, diagnostic.StatusCode);
        Assert.Equal("text/html", diagnostic.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), diagnostic.ResponseLength);
        Assert.DoesNotContain("person@example.test", diagnostic.Description);
        Assert.DoesNotContain("198.51.100.4", diagnostic.Description);
        Assert.True(diagnostic.Description.Length <= AsaasErrorSanitizer.MaximumTextLength);
    }

    [Fact]
    public async Task SensitiveFinancialAndCustomerDataNeverSurvivesSanitization()
    {
        string base64 = new('A', 120);
        string pixPayload = "000201" + new string('7', 90);
        string body =
            "name=Maria da Silva; address=Rua Secreta 123; " +
            "person@example.test +55 (34) 99918-7189 CPF 123.456.789-09 " +
            "CNPJ 12.345.678/0001-90 access_token=token-secret " +
            "apiKey=$aact_key-secret 203.0.113.77 " +
            "customer=cus_customer-secret payment=pay_payment-secret " +
            "qr=qrc_qr-secret externalReference=550e8400-e29b-41d4-a716-446655440000 " +
            $"pixKey=550e8400-e29b-41d4-a716-446655440001 {pixPayload} {base64}";
        using HttpResponseMessage response = Response(
            HttpStatusCode.BadRequest,
            body,
            "text/plain");

        AsaasErrorDiagnostic diagnostic = await AsaasErrorSanitizer.ReadAsync(response);

        string[] forbidden =
        {
            "Maria da Silva",
            "Rua Secreta",
            "person@example.test",
            "99918-7189",
            "123.456.789-09",
            "12.345.678/0001-90",
            "token-secret",
            "$aact_key-secret",
            "203.0.113.77",
            "cus_customer-secret",
            "pay_payment-secret",
            "qrc_qr-secret",
            "550e8400-e29b-41d4-a716-446655440000",
            "550e8400-e29b-41d4-a716-446655440001",
            pixPayload,
            base64
        };
        Assert.All(
            forbidden,
            sensitive => Assert.DoesNotContain(
                sensitive,
                diagnostic.Description,
                StringComparison.OrdinalIgnoreCase));
        Assert.True(diagnostic.Description.Length <= AsaasErrorSanitizer.MaximumTextLength);
        Assert.NotEqual(body, diagnostic.Description);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body,
        string mediaType = "application/json") =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
}
