using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api/gmail")]
public sealed class GmailController(FinSightDbContext db, IConfiguration configuration) : ControllerBase
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
    [HttpGet("connect")]
    public IActionResult Connect()
    {
        if (!configuration.IsGoogleConfigured())
        {
            return Redirect("/statements?gmail=unavailable");
        }

        if (User.IsDemoUser())
        {
            return Redirect("/statements?gmail=demo");
        }

        var properties = new GoogleChallengeProperties
        {
            RedirectUri = "/statements?gmail=connected",
            AccessType = "offline",
            Prompt = "consent",
            IncludeGrantedScopes = true,
            LoginHint = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
        };
        properties.SetScope("openid", "email", "profile", GoogleIntegrationOptions.GmailReadonlyScope);
        properties.Items[GoogleAccountLinker.IntentKey] = GoogleAccountLinker.GmailIntent;

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
