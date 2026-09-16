using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Core.Abstractions;
using FinSight.Infrastructure;
using FinSight.Infrastructure.Demo;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IConfiguration configuration,
    IOptions<DemoOptions> demoOptions,
    IGeminiService gemini,
    FinSightDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<SessionResponse> Session(CancellationToken cancellationToken)
    {
        var google = configuration.IsGoogleConfigured();
        var capabilities = new Capabilities(google, google, gemini.IsConfigured, demoOptions.Value.Enabled);

        if (User.Identity?.IsAuthenticated != true)
        {
            return new SessionResponse(false, null, capabilities);
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return user is null
            ? new SessionResponse(false, null, capabilities)
            : new SessionResponse(true, new SessionUser(user.Id, user.DisplayName, user.Email, user.IsDemo), capabilities with { Gmail = google && !user.IsDemo });
    }

    /// <summary>Browser navigation target: starts Google sign-in (basic profile only, no Gmail access).</summary>
    [AllowAnonymous]
    [HttpGet("google")]
    public IActionResult SignInWithGoogle([FromQuery] string? returnUrl)
    {
        if (!configuration.IsGoogleConfigured())
        {
            return Redirect("/?auth=unavailable");
        }

        var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
        properties.Items[GoogleAccountLinker.IntentKey] = GoogleAccountLinker.SignInIntent;
        return Challenge(properties, AuthenticationSetup.GoogleScheme);
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Demo)]
    [HttpPost("demo")]
    public async Task<ActionResult<SessionUser>> StartDemo([FromServices] DemoDataService demo, CancellationToken cancellationToken)
    {
        if (!demoOptions.Value.Enabled)
        {
            return Middleware.ApiErrors.Problem(StatusCodes.Status404NotFound, "demo_disabled", "Demo mode isn't available on this server.");
        }

        var user = await demo.CreateDemoUserAsync(cancellationToken);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            FinSightClaims.Create(user.Id, user.DisplayName, user.Email, isDemo: true, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = false });

        return new SessionUser(user.Id, user.DisplayName, user.Email, true);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    /// <summary>Only same-site relative paths, so the sign-in flow cannot be used as an open redirect.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
