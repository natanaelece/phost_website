using PremierAPI.Services;
using Xunit;

namespace PremierAPI.Tests;

public sealed class AdCredentialEmailServiceTests
{
    [Fact]
    public void BuildEmailHtml_ExplainsWebAndAnyDeskAccess()
    {
        string html = AdCredentialEmailService.BuildEmailHtml("Cliente", "cliente.ad");

        Assert.Contains("https://acesso.phost.pro", html);
        Assert.Contains("computador, celular ou tablet", html);
        Assert.Contains("mesmos dados de login do site", html);
        Assert.Contains("seu e-mail cadastrado e a mesma senha", html);
        Assert.Contains("através do AnyDesk", html);
    }

    [Fact]
    public void BuildEmailHtml_EncodesCustomerData()
    {
        string html = AdCredentialEmailService.BuildEmailHtml("<Cliente>", "usuario<script>");

        Assert.DoesNotContain("<Cliente>", html);
        Assert.DoesNotContain("usuario<script>", html);
        Assert.Contains("&lt;Cliente&gt;", html);
        Assert.Contains("usuario&lt;script&gt;", html);
    }
}
