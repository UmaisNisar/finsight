using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Email;

/// <summary>Links placed in email. Email is only offered when FinSight knows its public address, since every message needs an unsubscribe link.</summary>
public sealed class EmailLinks(IOptions<AppOptions> options, IHostEnvironment environment)
{
    /// <summary>The Vite dev server, which proxies /api to the API.</summary>
    public const string DevelopmentUrl = "http://localhost:5173";

    public Uri? AppUrl
    {
        get
        {
            var configured = options.Value.PublicUrl;
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = environment.IsDevelopment() ? DevelopmentUrl : null;
            }

            if (configured is null || !Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            {
                return null;
            }

            // Plain http only for local development addresses: links in email travel far.
            return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
        }
    }

    public string Unsubscribe(string token) => new Uri(AppUrl!, $"api/email/unsubscribe?token={Uri.EscapeDataString(token)}").AbsoluteUri;
}

/// <summary>Whether this server can send email at all: a working sender and a public address for links.</summary>
public sealed class EmailAvailability(IEmailSender sender, EmailLinks links)
{
    public bool IsConfigured => sender.IsConfigured && links.AppUrl is not null;
}

public enum UnsubscribeTokenStatus
{
    Valid,
    Expired,
    Invalid,
}

/// <summary>
/// Signed, expiring tokens for one-click unsubscribe, so a link in an email can turn off summaries without signing in.
/// Data Protection authenticates and encrypts the payload under its own purpose; the expiry is checked against
/// <see cref="TimeProvider"/>, so tests control it.
/// </summary>
public sealed class UnsubscribeTokens(IDataProtectionProvider provider, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    private readonly IDataProtector _protector = provider.CreateProtector("FinSight.DigestUnsubscribe.v1");

    public string Create(Guid userId)
    {
        var expires = time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        return _protector.Protect($"{userId:N}.{expires.ToString(CultureInfo.InvariantCulture)}");
    }

    public UnsubscribeTokenStatus Read(string? token, out Guid userId)
    {
        userId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024)
        {
            return UnsubscribeTokenStatus.Invalid;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return UnsubscribeTokenStatus.Invalid;
        }

        var parts = payload.Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return UnsubscribeTokenStatus.Invalid;
        }

        if (time.GetUtcNow().ToUnixTimeSeconds() > expires)
        {
            return UnsubscribeTokenStatus.Expired;
        }

        userId = id;
        return UnsubscribeTokenStatus.Valid;
    }
}
