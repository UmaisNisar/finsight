using System.Security.Claims;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Auth;

/// <summary>
/// Runs when Google redirects back. Two intents share the callback:
/// "signin" creates or finds the FinSight user; "gmail" attaches a Gmail grant to the user who is
/// already signed in. Tokens are encrypted into the database and never placed in the cookie.
/// </summary>
public sealed class GoogleAccountLinker(FinSightDbContext db, UserContext userContext, ITokenProtector tokenProtector, TimeProvider time)
{
    public const string IntentKey = "finsight:intent";
    public const string SignInIntent = "signin";
    public const string GmailIntent = "gmail";

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
            userContext.SetUser(userId);

            if (!hasGmail || string.IsNullOrEmpty(context.RefreshToken))
            {
                context.Properties.RedirectUri = "/statements?gmail=denied";
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
    }
}
