using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net;

namespace PremierAPI.Services;

public static class StartupConfigurationValidator
{
    private static readonly string[] RequiredKeys =
    {
        "ActiveDirectory:ActiveUsersOu",
        "ActiveDirectory:BaseDn",
        "ActiveDirectory:ComputersOu",
        "ActiveDirectory:ExpiredUsersOu",
        "ActiveDirectory:GroupsOu",
        "ActiveDirectory:Password",
        "ActiveDirectory:Port",
        "ActiveDirectory:RequiredLogonComputers",
        "ActiveDirectory:Server",
        "ActiveDirectory:User",
        "ActiveDirectory:WebsiteUsersOu",
        "AdminEmail",
        "AdminSecurity:SessionHours",
        "AdminSecurity:LoginChallengeMinutes",
        "AdminSecurity:TotpIssuer",
        "AdminSecurity:TotpSecretPath",
        "AdminToken",
        "Asaas:ApiKey",
        "Asaas:ApiToken",
        "Asaas:BaseUrl",
        "Asaas:SandBoxApiKey",
        "Asaas:SandBoxApiToken",
        "Asaas:SandBoxBaseUrl",
        "Asaas:UseSandbox",
        "Cloudflare:TurnstileSecretKey",
        "ConnectionStrings:DefaultConnection",
        "Evolution:AdminPhone",
        "Evolution:ApiKey",
        "Evolution:BaseUrl",
        "Evolution:DryRun",
        "Evolution:Instance",
        "PremierConfig:BaseUrlFront",
        "ReverseProxy:KnownProxy",
        "Smtp:FromEmail",
        "Smtp:FromName",
        "Smtp:Password",
        "Smtp:Port",
        "Smtp:Server",
        "Smtp:User",
        "Telegram:BotToken",
        "Telegram:ChatId",
        "Telegram:MinimumLevel"
    };

    public static IReadOnlyList<string> FindInvalidKeys(IConfiguration configuration)
    {
        var invalid = RequiredKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToHashSet(StringComparer.Ordinal);

        ValidatePositiveInteger(configuration, "ActiveDirectory:Port", invalid);
        ValidatePositiveInteger(configuration, "AdminSecurity:SessionHours", invalid);
        ValidatePositiveInteger(configuration, "AdminSecurity:LoginChallengeMinutes", invalid);
        ValidatePositiveInteger(configuration, "Smtp:Port", invalid);
        ValidateBoolean(configuration, "Asaas:UseSandbox", invalid);
        ValidateBoolean(configuration, "Evolution:DryRun", invalid);
        ValidateAbsoluteUri(configuration, "Asaas:BaseUrl", invalid);
        ValidateAbsoluteUri(configuration, "Asaas:SandBoxBaseUrl", invalid);
        ValidateAbsoluteUri(configuration, "Evolution:BaseUrl", invalid);
        ValidateAbsoluteUri(configuration, "PremierConfig:BaseUrlFront", invalid);
        ValidateIpAddress(configuration, "ReverseProxy:KnownProxy", invalid);
        ValidateAbsolutePath(configuration, "AdminSecurity:TotpSecretPath", invalid);

        if (!Enum.TryParse<LogLevel>(configuration["Telegram:MinimumLevel"], true, out var telegramMinimumLevel) ||
            !Enum.IsDefined(telegramMinimumLevel) ||
            telegramMinimumLevel > LogLevel.Error)
        {
            invalid.Add("Telegram:MinimumLevel");
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
        }
        catch
        {
            invalid.Add("ConnectionStrings:DefaultConnection");
        }

        return invalid.OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }

    private static void ValidatePositiveInteger(
        IConfiguration configuration,
        string key,
        ISet<string> invalid)
    {
        if (!int.TryParse(configuration[key], out int value) || value <= 0)
            invalid.Add(key);
    }

    private static void ValidateBoolean(
        IConfiguration configuration,
        string key,
        ISet<string> invalid)
    {
        if (!bool.TryParse(configuration[key], out _))
            invalid.Add(key);
    }

    private static void ValidateAbsoluteUri(
        IConfiguration configuration,
        string key,
        ISet<string> invalid)
    {
        if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            invalid.Add(key);
        }
    }

    private static void ValidateIpAddress(
        IConfiguration configuration,
        string key,
        ISet<string> invalid)
    {
        if (!IPAddress.TryParse(configuration[key], out _))
            invalid.Add(key);
    }

    private static void ValidateAbsolutePath(
        IConfiguration configuration,
        string key,
        ISet<string> invalid)
    {
        if (!Path.IsPathRooted(configuration[key]))
            invalid.Add(key);
    }
}
