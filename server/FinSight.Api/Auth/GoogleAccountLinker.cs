using System.Security.Claims;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinSight.Api.Auth;

/// <summary>
/// Runs when Google redirects back. Two intents share the callback:
/// "signin" creates or finds the FinSight user; "gmail" attaches a Gmail grant to the user who is
/// already signed in. Tokens are encrypted into the database and never placed in the cookie.
/// The Gmail grant may come from a different Google account than the one used to sign in (for example a separate
/// mailbox that receives statements); it is attached to the signed-in FinSight user either way.
/// </summary>
public sealed class GoogleAccountLinker(FinSightDbContext db, UserContext userContext, ITokenProtector tokenProtector, IMemoryCache cache, TimeProvider time)
{
    public const string IntentKey = "finsight:intent";
    public const string SignInIntent = "signin";
    public const string GmailIntent = "gmail";

    /// <summary>The FinSight user who started a Gmail connect, carried in the protected OAuth state.</summary>
    public const string InitiatingUserKey = "finsight:uid";

    /// <summary>Where a Gmail connect returns to (for example onboarding), carried in the protected OAuth state.</summary>
    public const string ReturnToKey = "finsight:returnTo";

    public const string DefaultGmailReturn = "/statements";

    /// <summary>The page a Gmail connect goes back to, with its outcome: <c>gmail=connected|denied|failed|unavailable|demo</c>.</summary>
    public static string GmailRedirect(string? returnTo, string outcome) =>
        ReturnUrls.WithQuery(ReturnUrls.Safe(returnTo, DefaultGmailReturn), "gmail", outcome);

    public static string GmailRedirect(AuthenticationProperties? properties, string outcome) =>
        GmailRedirect(properties?.Items.TryGetValue(ReturnToKey, out var returnTo) == true ? returnTo : null, outcome);

    public async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        var subject = context.Identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Google did not return a subject.");
        var email = context.Identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
        var name = context.Identity.FindFirst(ClaimTypes.GivenName)?.Value
            ?? context.Identity.FindFirst(ClaimTypes.Name)?.Value
            ?? email;

        var grantedScopes = context.TokenResponse.Response?.RootElement.TryGetProperty("scope", out var scope) == true
            ? scope.GetString() ?? string.Empty
            : string.Empty;
        var hasGmail = grantedScopes.Split(' ').Contains(GoogleIntegrationOptions.GmailReadonlyScope);
        var intent = context.Properties.Items.TryGetValue(IntentKey, out var value) ? value : SignInIntent;

        if (intent == GmailIntent)
        {
            var existing = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!existing.Succeeded || existing.Principal is null)
            {
                throw new InvalidOperationException("Connecting Gmail requires an active session.");
            }

            var userId = existing.Principal.GetUserId();

            // Another FinSight user signed in (in another tab) while Google's consent screen was open: don't hand them this grant.
            if (!context.Properties.Items.TryGetValue(InitiatingUserKey, out var initiator) || initiator != userId.ToString())
            {
                throw new InvalidOperationException("The signed-in user changed while connecting Gmail.");
            }

            userContext.SetUser(userId);

            if (existing.Principal.IsDemoUser())
            {
                // GmailController already refuses demo sessions; this keeps a grant from ever being stored for one.
                context.Properties.RedirectUri = GmailRedirect(context.Properties, "demo");
            }
            else if (!hasGmail || string.IsNullOrEmpty(context.RefreshToken))
            {
                // Google's granular consent lets the user untick Gmail while approving the rest.
                context.Properties.RedirectUri = GmailRedirect(context.Properties, "denied");
            }
            else
            {
                await SaveConnectionAsync(userId, email, grantedScopes, context.RefreshToken);
            }

            // Stay signed in as the same FinSight user, whichever Google account granted access.
            context.Principal = existing.Principal;
            return;
        }

        User? user;
        using (userContext.BeginSystemScope())
        {
            user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == subject);
        }

        if (user is null)
        {
            user = new User
            {
                GoogleSubject = subject,
                Email = email,
                DisplayName = name,
                CreatedAt = time.GetUtcNow(),
            };
            userContext.SetUser(user.Id);
            db.Users.Add(user);
        }
        else
        {
            userContext.SetUser(user.Id);
            user.Email = email;
            user.DisplayName = name;
        }

        await db.SaveChangesAsync();

        if (hasGmail && !string.IsNullOrEmpty(context.RefreshToken))
        {
            await SaveConnectionAsync(user.Id, email, grantedScopes, context.RefreshToken);
        }

        context.Principal = FinSightClaims.Create(user.Id, user.DisplayName, user.Email, isDemo: false, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private async Task SaveConnectionAsync(Guid userId, string email, string scopes, string refreshToken)
    {
        var connection = await db.GmailConnections.SingleOrDefaultAsync(c => c.UserId == userId);
        if (connection is null)
        {
            connection = new GmailConnection
            {
                UserId = userId,
                GoogleEmail = email,
                EncryptedRefreshToken = string.Empty,
                Scopes = scopes,
            };
            db.GmailConnections.Add(connection);
        }

        connection.GoogleEmail = email;
        connection.Scopes = scopes;
        connection.EncryptedRefreshToken = tokenProtector.Protect(refreshToken);
        connection.Status = GmailConnectionStatus.Active;
        connection.ConnectedAt = time.GetUtcNow();
        await db.SaveChangesAsync();

        // A reconnect may switch to another Google account; an access token cached for the old grant must not be reused.
        cache.Remove(GoogleTokenService.AccessTokenCacheKey(userId));
    }
}
