using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PremierAPI.Services;

public sealed class AdminTotpService
{
    private const int TotpDigits = 6;
    private const int TotpPeriodSeconds = 30;
    private readonly IDataProtector _protector;
    private readonly string _secretPath;
    private readonly string _issuer;
    private readonly string _accountName;
    private readonly object _stateLock = new();

    public AdminTotpService(IDataProtectionProvider dataProtectionProvider, IConfiguration configuration)
    {
        _protector = dataProtectionProvider.CreateProtector("PremierAPI.AdminTotp.v1");
        _secretPath = configuration["AdminSecurity:TotpSecretPath"]!;
        _issuer = configuration["AdminSecurity:TotpIssuer"]!;
        _accountName = configuration["AdminEmail"]!;
    }

    public bool IsEnrolled
    {
        get
        {
            lock (_stateLock)
                return LoadState() != null;
        }
    }

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public string FormatSecret(string secret)
    {
        return string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4)
            .Select(index => secret.Substring(index * 4, Math.Min(4, secret.Length - index * 4))));
    }

    public string CreateProvisioningUri(string secret)
    {
        string label = Uri.EscapeDataString($"{_issuer}:{_accountName}");
        string issuer = Uri.EscapeDataString(_issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits={TotpDigits}&period={TotpPeriodSeconds}";
    }

    public bool VerifySecret(string secret, string? code)
    {
        return VerifyTotp(secret, code, DateTimeOffset.UtcNow);
    }

    public TotpVerificationResult VerifyEnrolledCode(string? code)
    {
        lock (_stateLock)
        {
            var state = LoadState();
            if (state == null || string.IsNullOrWhiteSpace(code))
                return TotpVerificationResult.Invalid;

            if (VerifyTotp(state.Secret, code, DateTimeOffset.UtcNow))
                return new TotpVerificationResult(true, false, state.RecoveryCodeHashes.Count);

            string recoveryHash = HashRecoveryCode(code);
            int recoveryIndex = state.RecoveryCodeHashes.FindIndex(hash =>
                FixedTimeEquals(hash, recoveryHash));
            if (recoveryIndex < 0)
                return TotpVerificationResult.Invalid;

            state.RecoveryCodeHashes.RemoveAt(recoveryIndex);
            SaveState(state);
            return new TotpVerificationResult(true, true, state.RecoveryCodeHashes.Count);
        }
    }

    public IReadOnlyList<string> Activate(string secret)
    {
        lock (_stateLock)
        {
            if (LoadState() != null)
                throw new InvalidOperationException("A autenticação em dois fatores já está configurada.");

            string[] recoveryCodes = Enumerable.Range(0, 10)
                .Select(_ => GenerateRecoveryCode())
                .ToArray();
            SaveState(new AdminTotpState
            {
                Secret = secret,
                RecoveryCodeHashes = recoveryCodes.Select(HashRecoveryCode).ToList(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            return recoveryCodes;
        }
    }

    public static bool RunSelfTest()
    {
        const string rfcSecret = "12345678901234567890";
        var vectors = new (long UnixTime, string Code)[]
        {
            (59, "94287082"),
            (1111111109, "07081804"),
            (1111111111, "14050471"),
            (1234567890, "89005924"),
            (2000000000, "69279037"),
            (20000000000, "65353130")
        };

        byte[] secretBytes = Encoding.ASCII.GetBytes(rfcSecret);
        bool vectorsMatch = vectors.All(vector =>
            GenerateTotp(secretBytes, vector.UnixTime / TotpPeriodSeconds, 8) == vector.Code);
        bool base32RoundTrip = Base32Decode(Base32Encode(secretBytes)).SequenceEqual(secretBytes);
        return vectorsMatch && base32RoundTrip;
    }

    private static bool VerifyTotp(string secret, string? code, DateTimeOffset now)
    {
        string normalized = new((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalized.Length != TotpDigits || normalized.Length != (code ?? string.Empty).Trim().Length)
            return false;

        byte[] secretBytes;
        try
        {
            secretBytes = Base32Decode(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        long counter = now.ToUnixTimeSeconds() / TotpPeriodSeconds;
        for (long offset = -1; offset <= 1; offset++)
        {
            string expected = GenerateTotp(secretBytes, counter + offset, TotpDigits);
            if (FixedTimeEquals(normalized, expected))
                return true;
        }

        return false;
    }

    private static string GenerateTotp(byte[] secret, long counter, int digits)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        for (int index = 7; index >= 0; index--)
        {
            counterBytes[index] = (byte)(counter & 0xff);
            counter >>= 8;
        }

        using var hmac = new HMACSHA1(secret);
        byte[] hash = hmac.ComputeHash(counterBytes.ToArray());
        int offset = hash[^1] & 0x0f;
        int binaryCode = ((hash[offset] & 0x7f) << 24) |
                         ((hash[offset + 1] & 0xff) << 16) |
                         ((hash[offset + 2] & 0xff) << 8) |
                         (hash[offset + 3] & 0xff);
        int modulus = (int)Math.Pow(10, digits);
        return (binaryCode % modulus).ToString($"D{digits}");
    }

    private AdminTotpState? LoadState()
    {
        if (!File.Exists(_secretPath)) return null;
        string protectedState = File.ReadAllText(_secretPath, Encoding.UTF8);
        string json = _protector.Unprotect(protectedState);
        return JsonSerializer.Deserialize<AdminTotpState>(json)
            ?? throw new InvalidOperationException("Estado de autenticação em dois fatores inválido.");
    }

    private void SaveState(AdminTotpState state)
    {
        string? directory = Path.GetDirectoryName(_secretPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Diretório do segredo TOTP inválido.");

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string json = JsonSerializer.Serialize(state);
        string protectedState = _protector.Protect(json);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_secretPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, protectedState, Encoding.UTF8);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, _secretPath, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(_secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string GenerateRecoveryCode()
    {
        string raw = Base32Encode(RandomNumberGenerator.GetBytes(10));
        return string.Join('-', Enumerable.Range(0, 4).Select(index => raw.Substring(index * 4, 4)));
    }

    private static string HashRecoveryCode(string code)
    {
        string normalized = new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftBytes = Encoding.ASCII.GetBytes(left);
        byte[] rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (byte value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        string normalized = new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var output = new List<byte>(normalized.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char character in normalized)
        {
            int index = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1
            };
            if (index < 0) throw new FormatException("Segredo Base32 inválido.");

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }

    private sealed class AdminTotpState
    {
        public string Secret { get; set; } = string.Empty;
        public List<string> RecoveryCodeHashes { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; }
    }
}

public sealed record TotpVerificationResult(bool Success, bool UsedRecoveryCode, int RemainingRecoveryCodes)
{
    public static TotpVerificationResult Invalid { get; } = new(false, false, 0);
}
