namespace PremierAPI.Services;

public static class MetaBusinessEventPolicy
{
    public static bool ShouldSendCompleteRegistration(bool emailConfirmedNow) =>
        emailConfirmedNow;

    public static bool ShouldSendLead(bool requestPersistedNow) =>
        requestPersistedNow;

    public static bool ShouldSendStartTrial(
        bool transitionSucceeded,
        bool stateChanged,
        string? currentStatus) =>
        transitionSucceeded
        && stateChanged
        && string.Equals(currentStatus, "liberado", StringComparison.Ordinal);

    public static bool ShouldSendInitiateCheckout(bool orderPersisted, bool pixGenerated) =>
        orderPersisted && pixGenerated;

    public static bool ShouldSendPurchase(bool paymentReceived, bool orderMatched) =>
        paymentReceived && orderMatched;
}
