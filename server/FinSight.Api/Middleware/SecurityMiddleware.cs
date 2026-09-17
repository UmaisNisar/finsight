using FinSight.Api.Auth;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinSight.Api.Middleware;

/// <summary>Conservative security headers for the API and the single-page app it serves.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Financial responses must never be cached by browsers or proxies.
            headers.CacheControl = "no-store";
        }
        else
        {
            headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
                "script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        }

        return next(context);
    }
}

/// <summary>
/// Cross-site request forgery defence for cookie-authenticated APIs: state-changing requests must carry
/// a custom header, which browsers never add to cross-site form posts and which triggers a CORS
/// preflight (which this API does not allow) for cross-site scripts.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-FinSight-Request";

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var isSafe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

        // The unsubscribe link in summary emails is authorized by its signed token alone and reads no cookie, and mail clients
        // posting a one-click unsubscribe (RFC 8058) can't add headers.
        var tokenAuthorized = context.Request.Path.StartsWithSegments(FinSight.Api.Controllers.EmailController.UnsubscribePath);

        if (!isSafe && !tokenAuthorized && context.Request.Path.StartsWithSegments("/api") && context.Request.Headers[HeaderName] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiErrors.Create(403, "csrf_rejected", "This request was blocked for your security. Reload the page and try again."));
            return;
        }

        await next(context);
    }
}

/// <summary>
/// Binds the signed-in user to the request's <see cref="UserContext"/>, which scopes every database query.
/// Sessions that were signed out, outlived <see cref="SessionService.MaxLifetime"/>, or belong to users that no longer
/// exist (deleted account, expired demo) are ended.
/// </summary>
public sealed class UserContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserContext userContext, FinSightDbContext db, SessionService sessions, IMemoryCache cache)
    {
        // The unsubscribe endpoint acts only for the user named in its token, so a stale session in the same browser doesn't block it.
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst(FinSightClaims.UserId) is not null
            && !context.Request.Path.StartsWithSegments(FinSight.Api.Controllers.EmailController.UnsubscribePath))
        {
            var userId = context.User.GetUserId();
            userContext.SetUser(userId);

            var exists = await cache.GetOrCreateAsync($"user-exists:{userId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                return await db.Users.AnyAsync(u => u.Id == userId);
            });

            if (!exists || context.User.GetSessionId() is not { } sessionId || !await sessions.IsActiveAsync(sessionId, context.RequestAborted))
            {
                await context.SignOutAsync();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ApiErrors.Create(401, "session_ended", "Your session has ended. Sign in again."));
                return;
            }
        }

        await next(context);
    }
}
