using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinSight.Api.Controllers;

/// <summary>
/// The user's own Gemini API key. It is checked with Google before it is saved, stored encrypted, and never returned,
/// logged or placed in an error. Without one, AI features use the server key when there is one.
/// </summary>
// Also enforced by the fallback policy; explicit so every endpoint here visibly requires a signed-in user.
[Authorize]
[ApiController]
[Route("api/ai/key")]
public sealed class AiKeyController(FinSightDbContext db, IOptions<GeminiOptions> options) : ControllerBase
{
    private const int MinKeyLength = 20;
    private const int MaxKeyLength = 200;

    [HttpGet]
    public async Task<AiKeyStatusDto> Get(CancellationToken cancellationToken) =>
        ToDto(await db.Users.AsNoTracking().SingleAsync(cancellationToken));

    [HttpPut]
    [EnableRateLimiting(RateLimits.AiKey)]
    public async Task<ActionResult<AiKeyStatusDto>> Save(
        SaveAiKeyRequest request, [FromServices] GeminiKeyValidator validator, [FromServices] IApiKeyProtector protector, CancellationToken cancellationToken)
    {
        if (User.IsDemoUser())
        {
            return DemoMode();
        }

        var apiKey = request.ApiKey?.Trim() ?? string.Empty;
        if (apiKey.Length is < MinKeyLength or > MaxKeyLength || apiKey.Any(char.IsWhiteSpace))
        {
            return ApiErrors.BadRequest("invalid_api_key", "That doesn't look like a Gemini API key. Copy the whole key from Google AI Studio.");
        }

        switch (await validator.CheckAsync(apiKey, cancellationToken))
        {
            case GeminiKeyCheck.Rejected:
                return ApiErrors.BadRequest("invalid_api_key", "Google didn't accept that key. Check you copied the whole key.");
            case GeminiKeyCheck.RateLimited:
                return ApiErrors.Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Google is limiting requests for this key right now. Wait a minute and try again.");
            case GeminiKeyCheck.Unavailable:
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, "ai_unavailable", "We couldn't reach Google to check the key. Try again in a moment.");
        }

        var user = await db.Users.SingleAsync(cancellationToken);
        user.EncryptedGeminiApiKey = protector.Protect(apiKey);
        user.GeminiApiKeyHint = apiKey[^4..];
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        if (User.IsDemoUser())
        {
            return DemoMode();
        }

        var user = await db.Users.SingleAsync(cancellationToken);
        user.EncryptedGeminiApiKey = null;
        user.GeminiApiKeyHint = null;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private AiKeyStatusDto ToDto(User user) => new(
        user.EncryptedGeminiApiKey is not null,
        user.EncryptedGeminiApiKey is not null && user.GeminiApiKeyHint is not null ? "…" + user.GeminiApiKeyHint : null,
        options.Value.IsConfigured,
        options.Value.Model);

    private static ObjectResult DemoMode() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "demo_mode", "Demo mode uses the server's AI setup. Sign in with Google to add your own Gemini key.");
}
