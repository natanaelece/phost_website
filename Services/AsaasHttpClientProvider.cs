using System.Net.Http.Headers;

namespace PremierAPI.Services;

public static class AsaasHttpClientNames
{
    public const string Production = "AsaasProduction";
    public const string Sandbox = "AsaasSandbox";
    public const string UserAgent = "Premierhost-BFF/1.0";
}

public sealed class AsaasHttpClientProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly bool _useSandbox;

    public AsaasHttpClientProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _useSandbox = configuration.GetValue<bool>("Asaas:UseSandbox");
    }

    public HttpClient CreateCurrentClient() => CreateClient(_useSandbox);

    public HttpClient CreateClient(bool useSandbox) =>
        _httpClientFactory.CreateClient(
            useSandbox ? AsaasHttpClientNames.Sandbox : AsaasHttpClientNames.Production);
}

public static class AsaasHttpClientServiceCollectionExtensions
{
    private static readonly TimeSpan PreservedDefaultTimeout = TimeSpan.FromSeconds(100);

    public static IServiceCollection AddAsaasHttpClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddClient(
            services,
            AsaasHttpClientNames.Production,
            configuration["Asaas:BaseUrl"],
            configuration["Asaas:ApiKey"]);
        AddClient(
            services,
            AsaasHttpClientNames.Sandbox,
            configuration["Asaas:SandBoxBaseUrl"],
            configuration["Asaas:SandBoxApiKey"]);

        services.AddSingleton<AsaasHttpClientProvider>();
        services.AddSingleton<AsaasApiClient>();
        return services;
    }

    private static void AddClient(
        IServiceCollection services,
        string name,
        string? baseUrl,
        string? apiKey)
    {
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = NormalizeBaseAddress(baseUrl);
            client.Timeout = PreservedDefaultTimeout;
            client.DefaultRequestHeaders.Add("access_token", apiKey ?? string.Empty);
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(
                ProductInfoHeaderValue.Parse(AsaasHttpClientNames.UserAgent));
        });
    }

    internal static Uri NormalizeBaseAddress(string? baseUrl)
    {
        string normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/') + "/";
        return new Uri(normalized, UriKind.Absolute);
    }
}
