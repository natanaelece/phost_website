using System.Text.RegularExpressions;
using Xunit;

namespace PremierAPI.Tests;

public sealed class ClientAuthStaticSecurityTests
{
    [Fact]
    public void ProductionSessionSqlIsCentralizedAndUsesOnlyTokenHash()
    {
        string root = RepositoryRoot();
        string[] consumers = Directory
            .EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs"))
            .Where(path => File.ReadAllText(path).Contains(
                "user_sessions",
                StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(
            new[] { "ClientSessionService.cs", "DatabaseInitializer.cs" },
            consumers);

        string sessionService = File.ReadAllText(
            Path.Combine(root, "Services", "ClientSessionService.cs"));
        Assert.Contains("token_hash = @TokenHash", sessionService);
        Assert.DoesNotContain(" token = @Token", sessionService);
        Assert.DoesNotContain(
            "INSERT INTO user_sessions (user_id, token,",
            sessionService);
    }

    [Fact]
    public void ConfirmationSqlIsCentralizedAndSmtpRunsAfterDatabaseWork()
    {
        string root = RepositoryRoot();
        string auth = File.ReadAllText(
            Path.Combine(root, "Controllers", "AuthController.cs"));
        string admin = File.ReadAllText(
            Path.Combine(root, "Controllers", "AdminController.cs"));
        string reminder = File.ReadAllText(
            Path.Combine(root, "Services", "EmailConfirmationReminderWorker.cs"));
        string confirmationTokens = File.ReadAllText(
            Path.Combine(
                root,
                "Services",
                "EmailConfirmationTokenService.cs"));
        string production = string.Join('\n', auth, admin, reminder);

        Assert.DoesNotContain(
            "email_confirmation_token_hash",
            production);
        Assert.DoesNotContain(
            "email_confirmation_token =",
            production);
        Assert.DoesNotContain(
            "email_confirmation_tokens",
            production);
        Assert.Contains(
            "FROM email_confirmation_tokens t",
            confirmationTokens);
        Assert.Contains(
            "UPDATE email_confirmation_tokens",
            confirmationTokens);
        Assert.Contains("password_reset_token_hash = @TokenHash", production);
        Assert.DoesNotMatch(
            new Regex(@"WHERE\s+email_confirmation_token\s*=\s*@Token\b"),
            production);
        Assert.DoesNotMatch(
            new Regex(@"WHERE\s+password_reset_token\s*=\s*@Token\b"),
            production);
        Assert.DoesNotContain(
            "NpgsqlConnection",
            reminder);
        Assert.DoesNotContain(
            "NpgsqlTransaction",
            reminder);

        int registerStart = auth.IndexOf(
            "public async Task<IActionResult> Register",
            StringComparison.Ordinal);
        int confirmationStart = auth.IndexOf(
            "public async Task<IActionResult> ConfirmEmail",
            StringComparison.Ordinal);
        string registerFlow = auth[registerStart..confirmationStart];
        Assert.True(
            registerFlow.IndexOf(
                "transaction.CommitAsync",
                StringComparison.Ordinal) <
            registerFlow.IndexOf(
                "_emailConfirmation.SendAsync",
                StringComparison.Ordinal));
        Assert.True(
            registerFlow.IndexOf(
                "db.CloseAsync",
                StringComparison.Ordinal) <
            registerFlow.IndexOf(
                "_emailConfirmation.SendAsync",
                StringComparison.Ordinal));

        string adminConfirmationFlow = admin[
            admin.IndexOf(
                "public async Task<IActionResult> ConfirmEmailManual",
                StringComparison.Ordinal)
            ..
            admin.IndexOf(
                "private DateTime GetEmailConfirmationLocalNow",
                StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "BeginTransactionAsync",
            adminConfirmationFlow);
        Assert.Contains(
            "_emailConfirmation.SendAsync",
            adminConfirmationFlow);
        Assert.True(Regex.Matches(
            auth,
            Regex.Escape(
                "Se o e-mail estiver cadastrado, você receberá um link de recuperação."))
            .Count >= 2);

        string resetFlow = auth[
            auth.IndexOf(
                "public async Task<IActionResult> ResetPassword",
                StringComparison.Ordinal)
            ..
            auth.IndexOf(
                "// VALIDATE RESET TOKEN",
                StringComparison.Ordinal)];
        Assert.Contains(
            "_clientSessions.RevokeAllSessionsAsync",
            resetFlow);
        Assert.DoesNotContain(
            "_clientSessions.CreateSessionAsync",
            resetFlow);
        Assert.Single(
            Regex.Matches(
                auth,
                Regex.Escape("TrySendCompleteRegistrationAsync"))
                .Cast<Match>());
    }

    [Fact]
    public void ConfirmationProductionSqlExistsOnlyInCentralServiceAndMigration()
    {
        string root = RepositoryRoot();
        string[] consumers = Directory
            .EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs")
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "Services"),
                "*.cs"))
            .Where(path => File.ReadAllText(path).Contains(
                "email_confirmation_tokens",
                StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(
            new[]
            {
                "DatabaseInitializer.cs",
                "EmailConfirmationTokenService.cs"
            },
            consumers);
    }

    [Fact]
    public void PasswordChangeFrontendRequiresAndReplacesRotatedToken()
    {
        string root = RepositoryRoot();
        string panel = File.ReadAllText(
            Path.Combine(root, "wwwroot", "js", "painel.js"));
        string profile = File.ReadAllText(
            Path.Combine(root, "Controllers", "ProfileController.cs"));

        Assert.Contains("typeof data.token !== 'string'", panel);
        Assert.Contains(
            "localStorage.setItem('premier_token', data.token)",
            panel);
        Assert.DoesNotContain("premier_old_token", panel);
        Assert.Contains(
            "BCrypt.Net.BCrypt.Verify",
            profile);
        Assert.Contains(
            "req.CurrentPassword",
            profile);
        Assert.Contains(
            "_clientSessions.RotateSessionAsync",
            profile);
        Assert.Contains(
            "token = rotatedSession?.Token",
            profile);
    }

    [Fact]
    public void AuthTokenGenerationDoesNotUseGuid()
    {
        string root = RepositoryRoot();
        string[] files =
        {
            Path.Combine(root, "Controllers", "AuthController.cs"),
            Path.Combine(root, "Controllers", "ProfileController.cs"),
            Path.Combine(root, "Services", "ClientSessionService.cs"),
            Path.Combine(root, "Services", "SecurityTokenService.cs"),
            Path.Combine(root, "Services", "EmailConfirmationTokenService.cs"),
            Path.Combine(root, "Services", "EmailConfirmationReminderWorker.cs")
        };

        Assert.All(
            files,
            file => Assert.DoesNotContain(
                "Guid.NewGuid(",
                File.ReadAllText(file),
                StringComparison.Ordinal));
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
}
