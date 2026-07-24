using Microsoft.Extensions.Logging;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class SecurityTokenServiceTests
{
    private readonly SecurityTokenService _tokens = new();

    [Fact]
    public void GeneratedTokensUseCSPRNGLengthAndUrlSafeEncoding()
    {
        string first = _tokens.GenerateToken();
        string second = _tokens.GenerateToken();

        Assert.True(first.Length >= 43);
        Assert.True(first.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.True(first != second);
        Assert.True(_tokens.IsValidToken(
            first,
            SecurityTokenPurpose.ClientSession));
    }

    [Fact]
    public void Sha256HashIsDeterministicStableAndDifferentFromRawToken()
    {
        string raw = _tokens.GenerateToken();
        string first = _tokens.HashToken(raw);
        string second = _tokens.HashToken(raw);

        Assert.True(first == second);
        Assert.True(first != raw);
        Assert.True(SecurityTokenService.IsValidHash(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("contains+non-url-safe/value")]
    [InlineData(" leading-space")]
    [InlineData("trailing-space ")]
    public void InvalidTokensAreRejected(string raw)
    {
        Assert.False(_tokens.TryHashToken(
            raw,
            SecurityTokenPurpose.ClientSession,
            out _));
    }

    [Fact]
    public void LegacyFormatsRemainPurposeScopedDuringMigrationWindow()
    {
        string legacySession = new('a', 64);
        string legacyConfirmation = Guid.Empty.ToString("D");
        string legacyReset = new('b', 32);

        Assert.True(_tokens.IsValidToken(
            legacySession,
            SecurityTokenPurpose.ClientSession));
        Assert.True(_tokens.IsValidToken(
            legacyConfirmation,
            SecurityTokenPurpose.EmailConfirmation));
        Assert.True(_tokens.IsValidToken(
            legacyReset,
            SecurityTokenPurpose.PasswordReset));
        Assert.False(_tokens.IsValidToken(
            legacyReset,
            SecurityTokenPurpose.ClientSession));
    }

    [Fact]
    public void TokenServiceHasNoLoggingDependencyThatCouldCaptureSecrets()
    {
        var constructor = Assert.Single(typeof(SecurityTokenService).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        Assert.DoesNotContain(
            typeof(SecurityTokenService).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            field => typeof(ILogger).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void EmailFailureSanitizerDoesNotPreserveSensitiveExceptionText()
    {
        const string sensitiveMarker = "synthetic-sensitive-marker";
        var original = new TimeoutException(sensitiveMarker);

        Exception sanitized =
            EmailConfirmationFailureSanitizer.SafeException(original);

        Assert.Equal(
            "smtp_timeout",
            EmailConfirmationFailureSanitizer.Code(original));
        Assert.DoesNotContain(
            sensitiveMarker,
            sanitized.ToString(),
            StringComparison.Ordinal);
        Assert.Null(sanitized.InnerException);
    }
}
