using System.Security.Cryptography;
using System.Text;

namespace PremierAPI.Services;

public enum SecurityTokenPurpose
{
    ClientSession,
    EmailConfirmation,
    PasswordReset
}

public sealed class SecurityTokenService
{
    public const int TokenByteLength = 32;
    public const int Sha256HexLength = 64;
    private const int Base64UrlTokenLength = 43;

    public string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public bool TryHashToken(
        string? rawToken,
        SecurityTokenPurpose purpose,
        out string tokenHash)
    {
        tokenHash = string.Empty;
        if (!IsValidToken(rawToken, purpose)) return false;

        tokenHash = HashToken(rawToken!);
        return true;
    }

    public bool IsValidToken(string? rawToken, SecurityTokenPurpose purpose)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken != rawToken.Trim())
            return false;

        if (IsCurrentToken(rawToken)) return true;

        return purpose switch
        {
            SecurityTokenPurpose.ClientSession =>
                rawToken.Length == 64 && rawToken.All(Uri.IsHexDigit),
            SecurityTokenPurpose.EmailConfirmation =>
                Guid.TryParseExact(rawToken, "D", out _),
            SecurityTokenPurpose.PasswordReset =>
                rawToken.Length == 32 && rawToken.All(Uri.IsHexDigit),
            _ => false
        };
    }

    public string HashToken(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
    }

    public static bool IsValidHash(string? tokenHash) =>
        tokenHash?.Length == Sha256HexLength &&
        tokenHash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCurrentToken(string rawToken)
    {
        if (rawToken.Length != Base64UrlTokenLength ||
            rawToken.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            string base64 = rawToken.Replace('-', '+').Replace('_', '/') + "=";
            return Convert.FromBase64String(base64).Length == TokenByteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
