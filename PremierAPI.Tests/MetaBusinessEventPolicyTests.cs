using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class MetaBusinessEventPolicyTests
{
    [Fact]
    public void CompleteRegistration_RequiresEffectiveEmailConfirmation()
    {
        Assert.False(MetaBusinessEventPolicy.ShouldSendCompleteRegistration(false));
        Assert.True(MetaBusinessEventPolicy.ShouldSendCompleteRegistration(true));
    }

    [Fact]
    public void Lead_RequiresPersistedNewRequest()
    {
        Assert.False(MetaBusinessEventPolicy.ShouldSendLead(false));
        Assert.True(MetaBusinessEventPolicy.ShouldSendLead(true));
    }

    [Theory]
    [InlineData(false, true, "liberado")]
    [InlineData(true, false, "liberado")]
    [InlineData(true, true, "solicitado")]
    public void StartTrial_RejectsAnythingExceptFirstRealRelease(
        bool transitionSucceeded,
        bool stateChanged,
        string currentStatus)
    {
        Assert.False(MetaBusinessEventPolicy.ShouldSendStartTrial(
            transitionSucceeded,
            stateChanged,
            currentStatus));
    }

    [Fact]
    public void StartTrial_AllowsFirstRealRelease()
    {
        Assert.True(MetaBusinessEventPolicy.ShouldSendStartTrial(
            transitionSucceeded: true,
            stateChanged: true,
            currentStatus: "liberado"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InitiateCheckout_RequiresPersistedOrderAndValidPix(
        bool orderPersisted,
        bool pixGenerated)
    {
        Assert.False(MetaBusinessEventPolicy.ShouldSendInitiateCheckout(
            orderPersisted,
            pixGenerated));
    }

    [Fact]
    public void InitiateCheckout_AllowsPersistedOrderWithValidPix()
    {
        Assert.True(MetaBusinessEventPolicy.ShouldSendInitiateCheckout(
            orderPersisted: true,
            pixGenerated: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Purchase_RequiresReceivedPaymentAndMatchedOrder(
        bool paymentReceived,
        bool orderMatched)
    {
        Assert.False(MetaBusinessEventPolicy.ShouldSendPurchase(
            paymentReceived,
            orderMatched));
    }

    [Fact]
    public void Purchase_AllowsReceivedPaymentForMatchedOrder()
    {
        Assert.True(MetaBusinessEventPolicy.ShouldSendPurchase(
            paymentReceived: true,
            orderMatched: true));
    }
}
