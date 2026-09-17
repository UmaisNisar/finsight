using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Core.Abstractions;
using FinSight.Infrastructure;
using FinSight.Infrastructure.Demo;
using FinSight.Infrastructure.Gmail;
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
    IAuthenticationSchemeProvider schemes,
    IOptions<DemoOptions> demoOptions,
    IGeminiService gemini,
    FinSightDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<SessionResponse> Session(CancellationToken cancellationToken)
    {
        var google = await schemes.IsGoogleAvailableAsync();

        // Signed in, AI is available with the user's own key or the server key; signed out, only the server key counts.
        var capabilities = new Capabilities(google, google, await gemini.IsConfiguredAsync(cancellationToken), demoOptions.Value.Enabled);

        if (User.Identity?.IsAuthenticated != true)
        {
            return new SessionResponse(false, null, capabilities);
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return user is null
            ? new SessionResponse(false, null, capabilities)
            : new SessionResponse(true, ToSessionUser(user), capabilities with { Gmail = google && !user.IsDemo });
    }

    /// <summary>Browser navigation target: starts Google sign-in (basic profile only, no Gmail access).</summary>
    [AllowAnonymous]
    [HttpGet("google")]
    public async Task<IActionResult> SignInWithGoogle([FromQuery] string? returnUrl)
    {
        if (!await schemes.IsGoogleAvailableAsync())
        {
            return Redirect("/?auth=unavailable");
        }

        var properties = new AuthenticationProperties { RedirectUri = ReturnUrls.Safe(returnUrl) };
        properties.Items[GoogleAccountLinker.IntentKey] = GoogleAccountLinker.SignInIntent;
        return Challenge(properties, AuthenticationSetup.GoogleScheme);
    }

    /// <summary>
    /// Development only: whether Google OAuth is wired up and the exact redirect URI and origin to register in Google Cloud
    /// Console. Reports presence and shape of the credentials, never their values.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("diagnostics")]
    public async Task<ActionResult<AuthDiagnostics>> Diagnostics([FromServices] IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return Middleware.ApiErrors.Problem(StatusCodes.Status404NotFound, "not_found", "That endpoint doesn't exist.");
        }

        var settings = configuration.GetSection(GoogleIntegrationOptions.Section).Get<GoogleIntegrationOptions>() ?? new GoogleIntegrationOptions();
        var clientIdSet = !string.IsNullOrWhiteSpace(settings.ClientId);
        var clientIdFormatValid = clientIdSet && settings.ClientId!.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal);
        var clientSecretSet = !string.IsNullOrWhiteSpace(settings.ClientSecret);
        var handlerRegistered = await schemes.IsGoogleAvailableAsync();
        var restartRequired = clientIdSet && clientSecretSet && !handlerRegistered;

        var hints = new List<string>();
        if (!clientIdSet || !clientSecretSet)
        {
            hints.Add("Set Google:ClientId and Google:ClientSecret with dotnet user-secrets in server/FinSight.Api, then restart the API.");
        }

        if (clientIdSet && !clientIdFormatValid)
        {
            hints.Add("Google:ClientId should end with .apps.googleusercontent.com. Copy the client id, not the project id or number.");
        }

        if (restartRequired)
        {
            hints.Add("Both values are set but the API started without them. Restart the API.");
        }

        if (Request.Host.Port != 5173)
        {
            hints.Add("This request did not come through the Vite dev server. Open the app at http://localhost:5173 so the redirect URI matches.");
        }

        var origin = $"{Request.Scheme}://{Request.Host}";
        return new AuthDiagnostics(
            new GoogleDiagnostics(clientIdSet, clientIdFormatValid, clientSecretSet, handlerRegistered, restartRequired,
                origin, Request.GoogleRedirectUri(), ["openid", "email", "profile", GoogleIntegrationOptions.GmailReadonlyScope]),
            hints);
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

        return ToSessionUser(user);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private static SessionUser ToSessionUser(Core.Domain.User user) =>
        new(user.Id, user.DisplayName, user.Email, user.IsDemo, user.IsDemo || user.OnboardingCompletedAt is not null);
}
