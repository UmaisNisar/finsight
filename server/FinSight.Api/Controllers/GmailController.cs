using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

// Also enforced by the fallback policy; explicit so every endpoint here visibly requires a signed-in user.
[Authorize]
[ApiController]
[Route("api/gmail")]
public sealed class GmailController(FinSightDbContext db, IAuthenticationSchemeProvider schemes) : ControllerBase
{
    [HttpGet]
    public async Task<GmailConnectionDto> Get(CancellationToken cancellationToken)
    {
        var connection = await db.GmailConnections.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return connection is null
            ? new GmailConnectionDto(false, null, null, null, null)
            : new GmailConnectionDto(true, connection.GoogleEmail, connection.Status, connection.ConnectedAt, connection.LastSyncedAt);
    }

    /// <summary>
    /// Browser navigation target: asks Google for read-only Gmail access (incremental consent, offline
    /// access so syncing works later). The refresh token is stored encrypted server-side.
    /// </summary>
    /// <param name="returnTo">Same-site path to come back to, for example onboarding. Defaults to (and unsafe values fall back to) /statements.</param>
    [HttpGet("connect")]
    public async Task<IActionResult> Connect([FromQuery] string? returnTo)
    {
        returnTo = ReturnUrls.Safe(returnTo, GoogleAccountLinker.DefaultGmailReturn);

        if (!await schemes.IsGoogleAvailableAsync())
        {
            return Redirect(GoogleAccountLinker.GmailRedirect(returnTo, "unavailable"));
        }

        if (User.IsDemoUser())
        {
            return Redirect(GoogleAccountLinker.GmailRedirect(returnTo, "demo"));
        }

        var properties = new GoogleChallengeProperties
        {
            RedirectUri = GoogleAccountLinker.GmailRedirect(returnTo, "connected"),
            AccessType = "offline",
            Prompt = "consent",
            IncludeGrantedScopes = true,
            LoginHint = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        };
        properties.SetScope("openid", "email", "profile", GoogleIntegrationOptions.GmailReadonlyScope);
        properties.Items[GoogleAccountLinker.IntentKey] = GoogleAccountLinker.GmailIntent;
        properties.Items[GoogleAccountLinker.InitiatingUserKey] = User.GetUserId().ToString();
        properties.Items[GoogleAccountLinker.ReturnToKey] = returnTo;

        return Challenge(properties, AuthenticationSetup.GoogleScheme);
    }

    /// <summary>Revokes FinSight's Gmail access at Google and deletes the stored grant. Imported data is kept.</summary>
    [HttpDelete]
    public async Task<IActionResult> Disconnect([FromServices] GoogleTokenService tokens, CancellationToken cancellationToken)
    {
        await tokens.DisconnectAsync(User.GetUserId(), cancellationToken);
        return NoContent();
    }
}
