using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PremierAPI.Services;

namespace PremierAPI.Controllers;

[ApiController]
[Route("api/meta")]
[EnableRateLimiting("ApiPadrao")]
public sealed class MetaMarketingController : ControllerBase
{
    private static readonly HashSet<string> ContactNames = new(StringComparer.Ordinal)
    {
        "Atendimento Premier Host",
        "Canal de novidades"
    };

    private readonly MetaConversionsOptions _options;
    private readonly MetaAttributionService _attributions;
    private readonly MetaBusinessEventService _businessEvents;
    private readonly ILogger<MetaMarketingController> _logger;

    public MetaMarketingController(
        MetaConversionsOptions options,
        MetaAttributionService attributions,
        MetaBusinessEventService businessEvents,
        ILogger<MetaMarketingController> logger)
    {
        _options = options;
        _attributions = attributions;
        _businessEvents = businessEvents;
        _logger = logger;
    }

    [HttpGet("config")]
    public IActionResult GetBrowserConfiguration()
    {
        bool pixelEnabled = _options.DatasetId.Length > 0 && _options.DatasetId.All(char.IsDigit);
        return Ok(new
        {
            enabled = pixelEnabled,
            pixelId = pixelEnabled ? _options.DatasetId : null,
            consentVersion = _options.ConsentVersion
        });
    }

    [HttpPost("consent")]
    public async Task<IActionResult> SaveConsent(
        [FromBody] MetaConsentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AttributionId == Guid.Empty)
            return BadRequest(new { erro = "Identificador de atribuição inválido." });
        if (request.Status is not ("accepted" or "rejected"))
            return BadRequest(new { erro = "Preferência de marketing inválida." });

        try
        {
            await _attributions.CaptureAsync(
                BuildCapture(
                    request.AttributionId,
                    request.Status,
                    request.Fbp,
                    request.Fbc,
                    request.Fbclid,
                    request.SourceUrl),
                cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META CONSENTIMENTO] Falha ao persistir a preferência de marketing.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { erro = "Não foi possível salvar a preferência agora." });
        }
    }

    [HttpPost("events/browser")]
    public async Task<IActionResult> TrackBrowserEvent(
        [FromBody] MetaBrowserEventRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasAcceptedConsentCookie())
            return NoContent();
        if (request.AttributionId == Guid.Empty || !Guid.TryParse(request.EventId, out _))
            return BadRequest(new { erro = "Evento de marketing inválido." });

        IReadOnlyDictionary<string, object?> customData;
        string sourceUrl;
        if (request.EventName == "ViewContent")
        {
            customData = new Dictionary<string, object?>
            {
                ["content_name"] = "Guia WYD",
                ["content_category"] = "Conteúdo"
            };
            sourceUrl = "https://phost.pro/guia-wyd";
        }
        else if (request.EventName == "Contact" && ContactNames.Contains(request.ContentName ?? ""))
        {
            customData = new Dictionary<string, object?>
            {
                ["content_name"] = request.ContentName
            };
            sourceUrl = request.SourceUrl ?? "https://phost.pro/";
        }
        else
        {
            return BadRequest(new { erro = "Evento de marketing não permitido." });
        }

        try
        {
            await _attributions.CaptureAsync(
                BuildCapture(
                    request.AttributionId,
                    "accepted",
                    request.Fbp,
                    request.Fbc,
                    request.Fbclid,
                    request.SourceUrl),
                cancellationToken);

            MetaDeliveryResult delivery = await _businessEvents.TrySendBrowserEventAsync(
                request.EventName,
                request.EventId,
                sourceUrl,
                request.AttributionId,
                customData,
                cancellationToken);
            return Accepted(new { status = delivery.Status.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[META EVENTO] Falha ao processar o evento de navegador {EventName} ({EventId}).",
                request.EventName,
                request.EventId);
            return Accepted(new { status = MetaDeliveryStatus.Failed.ToString() });
        }
    }

    private MetaAttributionCapture BuildCapture(
        Guid attributionId,
        string status,
        string? fbp,
        string? fbc,
        string? fbclid,
        string? sourceUrl) =>
        new(
            attributionId,
            status,
            _options.ConsentVersion,
            fbp,
            fbc,
            fbclid,
            sourceUrl,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.FirstOrDefault(),
            Request.Headers["X-Session-Token"].FirstOrDefault());

    private bool HasAcceptedConsentCookie() =>
        Request.Cookies.TryGetValue(
            MetaAttributionService.ConsentCookieName,
            out string? consent) &&
        consent == "accepted" &&
        Request.Cookies.TryGetValue(
            MetaAttributionService.ConsentVersionCookieName,
            out string? version) &&
        version == _options.ConsentVersion;
}

public sealed class MetaConsentRequest
{
    public Guid AttributionId { get; set; }
    public string Status { get; set; } = "";
    public string? Fbp { get; set; }
    public string? Fbc { get; set; }
    public string? Fbclid { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class MetaBrowserEventRequest
{
    public Guid AttributionId { get; set; }
    public string EventName { get; set; } = "";
    public string EventId { get; set; } = "";
    public string? ContentName { get; set; }
    public string? Fbp { get; set; }
    public string? Fbc { get; set; }
    public string? Fbclid { get; set; }
    public string? SourceUrl { get; set; }
}
