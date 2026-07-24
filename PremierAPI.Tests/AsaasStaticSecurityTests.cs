using Xunit;

namespace PremierAPI.Tests;

public sealed class AsaasStaticSecurityTests
{
    [Fact]
    public void ProductionCodeContainsNoCertificateValidationBypass()
    {
        string root = RepositoryRoot();
        string[] forbidden =
        {
            "ServerCertificateCustomValidationCallback",
            "DangerousAcceptAnyServerCertificateValidator",
            "RemoteCertificateValidationCallback",
            "ServicePointManager.ServerCertificateValidationCallback",
            "sslPolicyErrors"
        };
        string[] productionFiles =
        {
            Path.Combine(root, "Program.cs"),
            Path.Combine(root, "Controllers", "AdminController.cs"),
            Path.Combine(root, "Controllers", "CheckoutController.cs"),
            Path.Combine(root, "Controllers", "WebhookController.cs"),
            Path.Combine(root, "Services", "AsaasApiClient.cs"),
            Path.Combine(root, "Services", "AsaasErrorSanitizer.cs"),
            Path.Combine(root, "Services", "AsaasHttpClientProvider.cs")
        };

        foreach (string file in productionFiles)
        {
            string source = File.ReadAllText(file);
            Assert.All(
                forbidden,
                value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ControllersDoNotConfigureAsaasCredentialsOrDirectCheckoutClient()
    {
        string root = RepositoryRoot();
        string checkout = File.ReadAllText(
            Path.Combine(root, "Controllers", "CheckoutController.cs"));
        string webhook = File.ReadAllText(
            Path.Combine(root, "Controllers", "WebhookController.cs"));
        string admin = File.ReadAllText(
            Path.Combine(root, "Controllers", "AdminController.cs"));

        Assert.DoesNotContain("\"access_token\"", checkout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"access_token\"", webhook, StringComparison.Ordinal);
        Assert.DoesNotContain("\"access_token\"", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("new HttpClient", checkout, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SyncAsaasCustomerAndDisableNotificationsAsync",
            webhook,
            StringComparison.Ordinal);
        Assert.Contains("AsaasApiClient", checkout, StringComparison.Ordinal);
        Assert.Contains("AsaasApiClient", webhook, StringComparison.Ordinal);
        Assert.Contains("AsaasApiClient", admin, StringComparison.Ordinal);
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
}
