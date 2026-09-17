using System.Net;
using FinSight.Infrastructure.Email;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

/// <summary>
/// One-click unsubscribe from monthly summary emails, without signing in. The signed, expiring token in the link is the
/// only credential: no session or cookie is read, so there's nothing for a cross-site request to borrow, and the token
/// can only switch summaries off. GET shows a confirmation page and changes nothing (link scanners follow GETs); POST
/// turns summaries off, from that page or straight from a mail client (RFC 8058 List-Unsubscribe-Post).
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/email/unsubscribe")]
public sealed class EmailController(UnsubscribeTokens tokens) : ControllerBase
{
    /// <summary>Excluded from the X-FinSight-Request header check: mail clients can't add custom headers.</summary>
    public const string UnsubscribePath = "/api/email/unsubscribe";

    [HttpGet]
    public IActionResult Confirm([FromQuery] string? token)
    {
        if (tokens.Read(token, out _) is var status && status != UnsubscribeTokenStatus.Valid)
        {
            return Page(StatusCodes.Status400BadRequest, "This link has expired",
                status == UnsubscribeTokenStatus.Expired
                    ? "Unsubscribe links work for 60 days. To stop summary emails, turn off Monthly summary email in FinSight Settings."
                    : "This unsubscribe link isn’t valid. To stop summary emails, turn off Monthly summary email in FinSight Settings.");
        }

        var form = $"""
            <form method="post" action="{UnsubscribePath}?token={WebUtility.HtmlEncode(Uri.EscapeDataString(token!))}">
            <button type="submit">Turn off summary emails</button>
            </form>
            """;
        return Page(StatusCodes.Status200OK, "Stop monthly summary emails?", "FinSight will stop emailing you a summary each month. You can turn it back on in Settings.", form);
    }

    [HttpPost]
    public async Task<IActionResult> Unsubscribe([FromQuery] string? token, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        var status = tokens.Read(token, out var userId);
        if (status != UnsubscribeTokenStatus.Valid)
        {
            return Page(StatusCodes.Status400BadRequest, "This link has expired",
                "To stop summary emails, turn off Monthly summary email in FinSight Settings.");
        }

        // A separate scope acting only for the token's user, whoever (if anyone) is signed in to this browser.
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            var user = await db.Users.SingleOrDefaultAsync(cancellationToken);
            if (user is not null && user.Settings.MonthlyDigestEnabled)
            {
                user.Settings.MonthlyDigestEnabled = false;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return Page(StatusCodes.Status200OK, "You’re unsubscribed", "FinSight won’t email you monthly summaries any more. You can turn them back on in Settings.");
    }

    private ContentResult Page(int status, string title, string message, string? action = null)
    {
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        Response.Headers["X-Robots-Tag"] = "noindex";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <title>{{WebUtility.HtmlEncode(title)}} · FinSight</title>
            <style>
            :root { color-scheme: light dark; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f5f5f7; color: #1d1d1f;
              font: 15px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; }
            main { box-sizing: border-box; width: min(420px, calc(100% - 32px)); padding: 28px; border-radius: 18px; background: #fff; }
            h1 { margin: 0 0 8px; font-size: 21px; font-weight: 600; }
            p { margin: 0; color: #6e6e73; }
            button { margin-top: 20px; padding: 10px 20px; border: 0; border-radius: 980px; background: #0071e3; color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
            @media (prefers-color-scheme: dark) {
              body { background: #000; color: #f5f5f7; }
              main { background: #1c1c1e; }
              p { color: #98989d; }
              button { background: #0a84ff; }
            }
            </style>
            </head>
            <body>
            <main>
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>{{WebUtility.HtmlEncode(message)}}</p>
            {{action}}
            </main>
            </body>
            </html>
            """;
        return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
    }
}
