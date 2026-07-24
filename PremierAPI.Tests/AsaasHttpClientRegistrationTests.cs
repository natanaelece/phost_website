using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class AsaasHttpClientRegistrationTests
{
    [Fact]
    public void ProductionAndSandboxClientsUseConfiguredBackendOnlySettings()
    {
        const string productionKey = "synthetic-production-key";
        const string sandboxKey = "synthetic-sandbox-key";
        IConfiguration configuration = Configuration(
            useSandbox: false,
            productionKey,
            sandboxKey);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddAsaasHttpClients(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        HttpClient production = factory.CreateClient(AsaasHttpClientNames.Production);
        HttpClient sandbox = factory.CreateClient(AsaasHttpClientNames.Sandbox);

        Assert.Equal("https://api.example.test/v3/", production.BaseAddress?.ToString());
        Assert.Equal("https://sandbox.example.test/v3/", sandbox.BaseAddress?.ToString());
        Assert.Equal(
            productionKey,
            Assert.Single(production.DefaultRequestHeaders.GetValues("access_token")));
        Assert.Equal(
            sandboxKey,
            Assert.Single(sandbox.DefaultRequestHeaders.GetValues("access_token")));
        Assert.Equal(
            AsaasHttpClientNames.UserAgent,
            production.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(
            AsaasHttpClientNames.UserAgent,
            sandbox.DefaultRequestHeaders.UserAgent.ToString());
        Assert.DoesNotContain(productionKey, production.BaseAddress!.ToString());
        Assert.DoesNotContain(sandboxKey, sandbox.BaseAddress!.ToString());
        Assert.Equal(TimeSpan.FromSeconds(100), production.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(100), sandbox.Timeout);
    }

    [Theory]
    [InlineData(false, "https://api.example.test/v3/")]
    [InlineData(true, "https://sandbox.example.test/v3/")]
    public void ProviderSelectsConfiguredEnvironment(bool useSandbox, string expectedBaseAddress)
    {
        IConfiguration configuration = Configuration(
            useSandbox,
            "production-key",
            "sandbox-key");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddAsaasHttpClients(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        HttpClient selected = provider
            .GetRequiredService<AsaasHttpClientProvider>()
            .CreateCurrentClient();

        Assert.Equal(expectedBaseAddress, selected.BaseAddress?.ToString());
    }

    [Fact]
    public void StartupValidationRequiresHttpsForBothAsaasEnvironments()
    {
        Dictionary<string, string?> values = ValidStartupValues();
        values["Asaas:BaseUrl"] = "http://api.example.test/v3";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        IReadOnlyList<string> invalid =
            StartupConfigurationValidator.FindInvalidKeys(configuration);

        Assert.Contains("Asaas:BaseUrl", invalid);
        Assert.DoesNotContain("Asaas:SandBoxBaseUrl", invalid);
    }

    private static IConfiguration Configuration(
        bool useSandbox,
        string productionKey,
        string sandboxKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asaas:BaseUrl"] = "https://api.example.test/v3",
                ["Asaas:SandBoxBaseUrl"] = "https://sandbox.example.test/v3/",
                ["Asaas:ApiKey"] = productionKey,
                ["Asaas:SandBoxApiKey"] = sandboxKey,
                ["Asaas:UseSandbox"] = useSandbox.ToString()
            })
            .Build();

    private static Dictionary<string, string?> ValidStartupValues() =>
        new()
        {
            ["ActiveDirectory:ActiveUsersOu"] = "OU=Active,DC=example,DC=test",
            ["ActiveDirectory:BaseDn"] = "DC=example,DC=test",
            ["ActiveDirectory:ComputersOu"] = "OU=Computers,DC=example,DC=test",
            ["ActiveDirectory:ExpiredUsersOu"] = "OU=Expired,DC=example,DC=test",
            ["ActiveDirectory:GroupsOu"] = "OU=Groups,DC=example,DC=test",
            ["ActiveDirectory:Password"] = "synthetic",
            ["ActiveDirectory:Port"] = "636",
            ["ActiveDirectory:RequiredLogonComputers"] = "SRV01",
            ["ActiveDirectory:Server"] = "ad.example.test",
            ["ActiveDirectory:User"] = "synthetic",
            ["ActiveDirectory:WebsiteUsersOu"] = "OU=Website,DC=example,DC=test",
            ["AdminEmail"] = "admin@example.test",
            ["AdminSecurity:SessionHours"] = "8",
            ["AdminSecurity:LoginChallengeMinutes"] = "5",
            ["AdminSecurity:TotpIssuer"] = "Synthetic",
            ["AdminSecurity:TotpSecretPath"] = "/tmp/synthetic-totp",
            ["AdminToken"] = "synthetic",
            ["Asaas:ApiKey"] = "synthetic",
            ["Asaas:ApiToken"] = "synthetic",
            ["Asaas:BaseUrl"] = "https://api.example.test/v3",
            ["Asaas:SandBoxApiKey"] = "synthetic",
            ["Asaas:SandBoxApiToken"] = "synthetic",
            ["Asaas:SandBoxBaseUrl"] = "https://sandbox.example.test/v3",
            ["Asaas:UseSandbox"] = "false",
            ["Cloudflare:TurnstileSecretKey"] = "synthetic",
            ["ConnectionStrings:DefaultConnection"] =
                "Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic",
            ["Evolution:AdminPhone"] = "5500000000000",
            ["Evolution:ApiKey"] = "synthetic",
            ["Evolution:BaseUrl"] = "https://evolution.example.test",
            ["Evolution:DryRun"] = "true",
            ["Evolution:Instance"] = "synthetic",
            ["PremierConfig:BaseUrlFront"] = "https://phost.example.test",
            ["ReverseProxy:KnownProxy"] = "127.0.0.1",
            ["Smtp:FromEmail"] = "noreply@example.test",
            ["Smtp:FromName"] = "Synthetic",
            ["Smtp:Password"] = "synthetic",
            ["Smtp:Port"] = "587",
            ["Smtp:Server"] = "smtp.example.test",
            ["Smtp:User"] = "synthetic",
            ["Telegram:BotToken"] = "synthetic",
            ["Telegram:ChatId"] = "1",
            ["Telegram:MinimumLevel"] = "Warning"
        };
}
