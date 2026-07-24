namespace PremierAPI.Services;

public sealed record MetaAttributionContext(
    Guid AttributionId,
    bool MarketingConsentGranted,
    string ConsentVersion,
    string? ClientIpAddress,
    string? ClientUserAgent,
    string? Fbp,
    string? Fbc,
    string? Email,
    string? Phone,
    string? FirstName,
    string? LastName,
    string? ExternalId);

public sealed record MetaConversionEvent(
    string EventName,
    string EventId,
    string EventSourceUrl,
    MetaAttributionContext Attribution,
    IReadOnlyDictionary<string, object?>? CustomData = null,
    DateTimeOffset? OccurredAt = null);

public enum MetaDeliveryStatus
{
    Sent,
    Duplicate,
    SkippedWithoutConsent,
    Disabled,
    Failed
}

public sealed record MetaDeliveryResult(MetaDeliveryStatus Status)
{
    public bool Sent => Status == MetaDeliveryStatus.Sent;
}
