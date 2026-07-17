using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PremierAPI.Services;

namespace PremierAPI.Controllers
{
    [ApiController]
    [Route("api/free-trial")]
    [EnableRateLimiting("ApiPadrao")]
    public sealed class FreeTrialController : ControllerBase
    {
        private readonly FreeTrialService _freeTrials;
        private readonly ILogger<FreeTrialController> _logger;

        public FreeTrialController(FreeTrialService freeTrials, ILogger<FreeTrialController> logger)
        {
            _freeTrials = freeTrials;
            _logger = logger;
        }

        [HttpGet("me")]
        public async Task<IActionResult> GetMine()
        {
            var status = await _freeTrials.GetMineAsync(GetSessionToken());
            return status == null
                ? Unauthorized(new { erro = "Sessão expirada." })
                : Ok(status);
        }

        [HttpPost("request")]
        public async Task<IActionResult> RequestTrial()
        {
            var result = await _freeTrials.RequestAsync(GetSessionToken());
            if (result == null) return Unauthorized(new { erro = "Sessão expirada." });
            if (result.IneligibleDueToPaidOrder)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    erro = "O teste grátis é exclusivo para novos usuários.",
                    status = result.Status
                });
            }
            if (result.AlreadyUsed)
            {
                return Conflict(new
                {
                    erro = "O teste grátis já foi utilizado por esta conta.",
                    status = result.Status
                });
            }
            if (string.Equals(result.Status.Status, "recusado", StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    erro = "A solicitação de teste grátis não foi liberada.",
                    status = result.Status
                });
            }

            _logger.LogInformation(
                "[TESTE GRATIS] Solicitação {RequestId} registrada/consultada. Nova: {Created}.",
                result.Status.RequestId, result.Created);

            var payload = new
            {
                msg = result.Created ? "Solicitação de teste grátis registrada." : "A solicitação já está registrada.",
                status = result.Status
            };
            return result.Created ? StatusCode(StatusCodes.Status201Created, payload) : Ok(payload);
        }

        private string? GetSessionToken()
        {
            return Request.Headers["X-Session-Token"].FirstOrDefault();
        }
    }
}
