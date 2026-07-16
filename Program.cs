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

var builder = WebApplication.CreateBuilder(args);

// CONFIGURAÇÃO: Carrega as configurações do sistema
builder.Configuration.AddEnvironmentVariables();

// SEGURANÇA: Limita tamanho máximo do body (protege RAM do T8 Pro)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 128 * 1024; // 128 KB
});

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PremierAPI.Services.ActiveDirectoryService>();
builder.Services.AddSingleton<PremierAPI.Services.WhatsAppTemplateService>();
builder.Services.AddSingleton<PremierAPI.Services.EmailConfirmationService>();
builder.Services.AddSingleton<PremierAPI.Services.AdCredentialEmailService>();
builder.Services.AddSingleton<PremierAPI.Services.AdAccountProvisioningService>();
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
    // Regra por IP para endpoints de autenticação (anti força bruta)
    // Máximo 5 tentativas por minuto por IP
    options.AddFixedWindowLimiter("AuthLimiter", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 5;
        opt.QueueLimit = 0;
        opt.AutoReplenishment = true;
    });

    // Regra padrão para outras APIs
    options.AddFixedWindowLimiter("ApiPadrao", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(10);
        opt.PermitLimit = 30;
        opt.QueueLimit = 0;
    });

    // Proteção global: Máximo 50 conexões simultâneas processando ao mesmo tempo
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

var tgToken = builder.Configuration["Telegram:BotToken"];
var tgChatId = builder.Configuration["Telegram:ChatId"];
var tgMinimumLevelName = builder.Configuration["Telegram:MinimumLevel"] ?? "Warning";
if (!Enum.TryParse<LogLevel>(tgMinimumLevelName, true, out var tgMinimumLevel))
{
    tgMinimumLevel = LogLevel.Warning;
}

if (!string.IsNullOrEmpty(tgToken) && !string.IsNullOrEmpty(tgChatId))
{
    builder.Logging.AddProvider(new TelegramLoggerProvider(tgToken, tgChatId, tgMinimumLevel));
}

// RUN DB INITIALIZER (CRIA TABELAS SE NÃO EXISTIREM)
PremierAPI.Services.DatabaseInitializer.Initialize(builder.Configuration);

var app = builder.Build();

var requiredConfigKeys = new[]
{
    "ConnectionStrings:DefaultConnection",
    "Cloudflare:TurnstileSecretKey",
    "Smtp:Server",
    "Smtp:Port",
    "Smtp:User",
    "Smtp:Password",
    "Smtp:FromName",
    "Smtp:FromEmail",
    "Asaas:ApiKey",
    "Asaas:ApiToken",
    "Asaas:BaseUrl",
    "Asaas:SandBoxBaseUrl",
    "Evolution:BaseUrl",
    "Evolution:Instance",
    "Evolution:ApiKey",
    "PremierConfig:BaseUrlFront"
};

var missingConfigs = requiredConfigKeys
    .Where(key => string.IsNullOrWhiteSpace(app.Configuration[key]))
    .ToArray();

if (missingConfigs.Length > 0)
{
    app.Logger.LogCritical(
        "[CONFIG ERRO FATAL] As seguintes variaveis de ambiente (ou configs) estao ausentes e sao OBRIGATORIAS: {Variables}. O servico entrara em modo de PAUSA para evitar loop de reinicializacao.",
        string.Join(", ", missingConfigs));
    await Task.Delay(-1); // Pausa infinitamente (não dá crash, evita que o systemd fique reiniciando em loop)
}

// CLOUDFLARE E PROXY REVERSO: Garante que o IP real do cliente chegue na API em vez do IP do Cloudflare (Evita falso Rate Limit no Asaas)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// 2. HEADERS DE SEGURANÇA: Previne XSS, Clickjacking, Sniffing e data injection
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // CSP: bloqueia scripts/estilos externos não autorizados
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://challenges.cloudflare.com https://cdn.jsdelivr.net https://cdn.tailwindcss.com https://fonts.googleapis.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://fonts.gstatic.com https://cdn.tailwindcss.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
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
app.UseStaticFiles();

// SECURITY: Apply CORS and RateLimiter to the request pipeline BEFORE MapControllers
app.UseCors("FrontendOnly");
app.UseRateLimiter();

app.MapGet("/recuperar-senha", async context =>
{
    var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
    var filePath = Path.Combine(env.WebRootPath, "recuperar-senha.html");

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(filePath);
});

// Aplica rate limiting de autenticação nos endpoints sensíveis
app.MapControllers().RequireRateLimiting("ApiPadrao");
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogWarning("[TG-INFO] 🚀 Serviço da PremierHost foi iniciado!");
});

app.Run("http://0.0.0.0:5000");




