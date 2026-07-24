using System.Text.RegularExpressions;

namespace PremierAPI.Services;

public sealed class MetaConversionsOptions
{
    private static readonly Regex GraphVersionPattern = new(
        @"^v\d+\.\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string DatasetId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string GraphApiVersion { get; init; } = "v25.0";
    public string? TestEventCode { get; init; }
    public string ConsentVersion { get; init; } = "1";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsConfigured =>
        DatasetId.Length > 0 &&
        DatasetId.All(char.IsDigit) &&
        !string.IsNullOrWhiteSpace(AccessToken) &&
        GraphVersionPattern.IsMatch(GraphApiVersion);

    public static MetaConversionsOptions FromConfiguration(IConfiguration configuration)
    {
        string configuredVersion =
            configuration["META_GRAPH_API_VERSION"] ??
            configuration["Meta:GraphApiVersion"] ??
            "v25.0";
        if (!GraphVersionPattern.IsMatch(configuredVersion))
            configuredVersion = "v25.0";

        int timeoutSeconds = configuration.GetValue<int?>("Meta:TimeoutSeconds") ?? 5;
        return new MetaConversionsOptions
        {
            DatasetId = (configuration["META_DATASET_ID"] ?? configuration["Meta:DatasetId"] ?? "").Trim(),
            AccessToken = configuration["META_CAPI_ACCESS_TOKEN"] ?? configuration["Meta:AccessToken"] ?? "",
            GraphApiVersion = configuredVersion,
            TestEventCode = NullIfWhiteSpace(
                configuration["META_CAPI_TEST_EVENT_CODE"] ?? configuration["Meta:TestEventCode"]),
            ConsentVersion = (configuration["Meta:ConsentVersion"] ?? "1").Trim(),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 15))
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
