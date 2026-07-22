using Microsoft.AspNetCore.HttpOverrides;
using PremierAPI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.RateLimiting;
using System;
using System.IO;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// CONFIGURAÇÃO: Carrega as configurações do sistema
builder.Configuration.AddEnvironmentVariables();

var invalidConfigKeys = StartupConfigurationValidator.FindInvalidKeys(builder.Configuration);
if (invalidConfigKeys.Count > 0)
{
    string invalidKeyList = string.Join(", ", invalidConfigKeys);
    const string prefix = "Falha de configuração na inicialização. Chaves ausentes ou inválidas: ";
    Console.Error.WriteLine($"[CONFIG ERRO FATAL] {prefix}{invalidKeyList}");
    await BootstrapTelegramNotifier.TrySendAsync(builder.Configuration, prefix + invalidKeyList);
    Environment.ExitCode = 78;
    return;
}

if (args.Contains("--validate-configuration", StringComparer.Ordinal))
{
    Console.WriteLine("CONFIGURATION_VALIDATION=PASS");
    return;
}

if (args.Contains("--validate-admin-security", StringComparer.Ordinal))
{
    if (!AdminTotpService.RunSelfTest())
    {
        Console.Error.WriteLine("ADMIN_SECURITY_VALIDATION=FAIL");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("ADMIN_SECURITY_VALIDATION=PASS");
    return;
}

var knownProxyAddress = IPAddress.Parse(builder.Configuration["ReverseProxy:KnownProxy"]!);

// SEGURANÇA: Limita tamanho máximo do body (protege RAM do T8 Pro)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 128 * 1024; // 128 KB
});

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(180);
    options.IncludeSubDomains = false;
    options.Preload = false;
});
DataProtectionConfiguration.AddPremierDataProtection(
    builder.Services,
    builder.Configuration,
    builder.Environment.ContentRootPath);
builder.Services.AddSingleton<PremierAPI.Services.ActiveDirectoryService>();
builder.Services.AddSingleton<PremierAPI.Services.WhatsAppTemplateService>();
builder.Services.AddScoped<PremierAPI.Services.FreeTrialService>();
builder.Services.AddSingleton<PremierAPI.Services.EmailConfirmationService>();
builder.Services.AddSingleton<PremierAPI.Services.AdCredentialEmailService>();
builder.Services.AddSingleton<PremierAPI.Services.AdPasswordProtectionService>();
builder.Services.AddSingleton<PremierAPI.Services.AdAccountProvisioningService>();
builder.Services.AddSingleton<PremierAPI.Services.AdminMaintenanceService>();
builder.Services.AddSingleton<PremierAPI.Services.AdminSessionService>();
builder.Services.AddSingleton<PremierAPI.Services.AdminTotpService>();
builder.Services.AddHostedService<PremierAPI.Services.AdAccountProvisioningWorker>();
builder.Services.AddHostedService<PremierAPI.Services.AdOrderExpirationWorker>();
builder.Services.AddHostedService<PremierAPI.Services.EmailConfirmationReminderWorker>();

// SEGURANÇA: CORS — só aceita requisições vindas do domínio oficial
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendOnly", policy =>
        policy.WithOrigins("https://phost.pro", "https://www.phost.pro")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// 1. RATE LIMITING & CONCURRENCY: Defesa interna para o hardware do T8 Pro
builder.Services.AddRateLimiter(options =>
{
    // Regra por IP para endpoints de autenticação (anti força bruta).
    // Cada cliente recebe sua própria janela para evitar bloqueio global.
    options.AddPolicy("AuthLimiter", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 5,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    // Regra padrão para outras APIs
    options.AddPolicy("ApiPadrao", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                PermitLimit = 30,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    // Proteção global: máximo de 20 requisições simultâneas por IP.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 20,
                QueueLimit = 0
            }));

    // Retorna HTTP 429 Too Many Requests de forma leve
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        return ValueTask.CompletedTask;
    };
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var adminLogStore = new AdminLogStore();
builder.Services.AddSingleton(adminLogStore);
builder.Logging.AddProvider(new AdminLogProvider(adminLogStore));

var tgToken = builder.Configuration["Telegram:BotToken"];
var tgChatId = builder.Configuration["Telegram:ChatId"];
var tgMinimumLevelName = builder.Configuration["Telegram:MinimumLevel"] ?? "Warning";
if (!Enum.TryParse<LogLevel>(tgMinimumLevelName, true, out var tgMinimumLevel))
{
    tgMinimumLevel = LogLevel.Warning;
}

if (!string.IsNullOrWhiteSpace(tgToken) && !string.IsNullOrWhiteSpace(tgChatId))
{
    builder.Logging.AddProvider(new TelegramLoggerProvider(tgToken, tgChatId, tgMinimumLevel));
}

var app = builder.Build();

try
{
    // RUN DB INITIALIZER (CRIA TABELAS SE NÃO EXISTIREM)
    PremierAPI.Services.DatabaseInitializer.Initialize(app.Configuration);
}
catch (Exception ex)
{
    const string message = "Falha crítica ao inicializar o banco de dados. Consulte o journal do serviço.";
    app.Logger.LogCritical(ex, "[DATABASE INIT ERRO FATAL] A inicialização do banco falhou.");
    await BootstrapTelegramNotifier.TrySendAsync(app.Configuration, message);
    Environment.ExitCode = 70;
    return;
}

// A origem aceita somente o proxy reverso autorizado e chamadas locais de health check.
// Esta barreira também protege a aplicação se a regra de firewall for removida por engano.
app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    bool isAuthorizedProxy = remoteAddress != null &&
        remoteAddress.MapToIPv6().Equals(knownProxyAddress.MapToIPv6());

    if (remoteAddress == null || (!IPAddress.IsLoopback(remoteAddress) && !isAuthorizedProxy))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

// CLOUDFLARE E PROXY REVERSO: aceita os cabeçalhos encaminhados somente do proxy conhecido.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardedForHeaderName = "CF-Connecting-IP",
    ForwardLimit = 1
};
forwardedHeadersOptions.KnownProxies.Add(knownProxyAddress);
forwardedHeadersOptions.AllowedHosts.Add("phost.pro");
forwardedHeadersOptions.AllowedHosts.Add("www.phost.pro");
forwardedHeadersOptions.AllowedHosts.Add("webhook-website.phost.pro");
app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseHsts();

// 2. HEADERS DE SEGURANÇA: Previne XSS, Clickjacking, Sniffing e data injection
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["CDN-Cache-Control"] = "no-store";
        context.Response.Headers["Cloudflare-CDN-Cache-Control"] = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // CSP: bloqueia scripts/estilos externos não autorizados
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "script-src 'self' https://challenges.cloudflare.com; " +
        "script-src-attr 'none'; " +
        "style-src 'self'; " +
        "style-src-attr 'none'; " +
        "font-src 'self'; " +
        "img-src 'self' data: https://phost.pro https://www.phost.pro https://challenges.cloudflare.com; " +
        "media-src 'self' https://phost.pro https://www.phost.pro; " +
        "frame-src https://challenges.cloudflare.com https://*.cloudflare.com; " +
        "connect-src 'self' https://challenges.cloudflare.com https://*.cloudflare.com; ";
    await next();
});

// =========================================================================

// =========================================================================
// URL CLEANER: Remove .html das URLs publicas e serve arquivos corretamente
// CASO 1: /painel.html -> redirect 301 para /painel
// CASO 2: /painel -> reescreve internamente para /painel.html (para servir)
// Ambos em um unico middleware para nao criar loop
// =========================================================================
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // CASO 1: URL termina com .html -> redireciona para URL limpa (sem .html)
    if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        string cleanPath = path[..^5]; // remove ".html"
        if (string.IsNullOrEmpty(cleanPath) || cleanPath == "/index") cleanPath = "/";
        context.Response.Redirect(cleanPath, permanent: true);
        return;
    }

    // CASO 2: URL sem extensao (nao API, nao raiz) -> reescreve internamente para .html
    if (!string.IsNullOrEmpty(path) && !path.Contains(".") && path != "/" && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        var filePath = System.IO.Path.Combine(builder.Environment.WebRootPath, path.TrimStart('/') + ".html");
        if (System.IO.File.Exists(filePath))
        {
            context.Request.Path = path + ".html";
        }
    }

    await next();
});
// =========================================================================

app.UseDefaultFiles();
var applicationInstance = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var applicationFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".html", ".css", ".js", ".json", ".xml", ".txt", ".map", ".webmanifest"
};

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var requestPath = context.Context.Request.Path.Value ?? string.Empty;
        var extension = Path.GetExtension(requestPath);
        var immutableAdminAsset = System.Text.RegularExpressions.Regex.IsMatch(
            requestPath,
            @"^/admin/assets/build/admin\.[a-f0-9]{12}\.min\.(css|js)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var immutablePublicAsset = System.Text.RegularExpressions.Regex.IsMatch(
            requestPath,
            @"^/assets/build/public\.[a-z0-9-]+\.[a-f0-9]{12}\.(css|js)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var edgeCachedPublicHtml = requestPath.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
            requestPath.Equals("/painel.html", StringComparison.OrdinalIgnoreCase) ||
            requestPath.Equals("/privacidade.html", StringComparison.OrdinalIgnoreCase);
        if (immutableAdminAsset || immutablePublicAsset)
        {
            // O hash no nome muda junto com o conteudo, portanto navegador e edge
            // podem manter estes arquivos por um ano sem servir versao obsoleta.
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Context.Response.Headers["Cloudflare-CDN-Cache-Control"] = "public, max-age=31536000, immutable";
            context.Context.Response.Headers["CDN-Cache-Control"] = "public, max-age=31536000, immutable";
        }
        else if (applicationFileExtensions.Contains(extension))
        {
            // O navegador nunca armazena arquivos mutaveis. A Cloudflare pode manter
            // por poucos segundos somente as tres paginas publicas allowlisted.
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate, max-age=0";
            context.Context.Response.Headers["Cloudflare-CDN-Cache-Control"] = edgeCachedPublicHtml
                ? "public, max-age=60, stale-while-revalidate=30"
                : "no-store";
            context.Context.Response.Headers["CDN-Cache-Control"] = "no-store";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
            context.Context.Response.Headers["X-Premier-Instance"] = applicationInstance;
        }
    }
});

// SECURITY: Apply CORS and RateLimiter to the request pipeline BEFORE MapControllers
app.UseCors("FrontendOnly");
app.UseRateLimiter();

app.MapGet("/recuperar-senha", async context =>
{
    var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
    var filePath = Path.Combine(env.WebRootPath, "recuperar-senha.html");

    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate, max-age=0";
    context.Response.Headers["Cloudflare-CDN-Cache-Control"] = "no-store";
    context.Response.Headers["CDN-Cache-Control"] = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    await context.Response.SendFileAsync(filePath);
});

// Aplica rate limiting de autenticação nos endpoints sensíveis
app.MapControllers().RequireRateLimiting("ApiPadrao");
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("[INICIALIZACAO] Serviço da PremierHost foi iniciado.");
});

app.Run("http://0.0.0.0:5000");




