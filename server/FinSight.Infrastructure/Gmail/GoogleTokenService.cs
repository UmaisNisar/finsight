using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Gmail;

/// <summary>The stored Gmail grant was revoked or expired. The user must reconnect.</summary>
public sealed class GmailAuthExpiredException : Exception
{
    public GmailAuthExpiredException() : base("Gmail connection expired.") { }
    public GmailAuthExpiredException(string message) : base(message) { }
    public GmailAuthExpiredException(string message, Exception inner) : base(message, inner) { }
}

public sealed class GmailNotConnectedException : Exception
{
    public GmailNotConnectedException() : base("Gmail is not connected.") { }
    public GmailNotConnectedException(string message) : base(message) { }
    public GmailNotConnectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exchanges the encrypted refresh token for short-lived access tokens. Access tokens live only in
/// server memory and are never persisted or sent to the browser.
/// </summary>
public sealed partial class GoogleTokenService(
    HttpClient http,
    FinSightDbContext db,
    ITokenProtector tokenProtector,
    IMemoryCache cache,
    IOptions<GoogleIntegrationOptions> options,
    ILogger<GoogleTokenService> logger)
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    public async Task<string> GetAccessTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = $"gmail-access:{userId}";
        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        var connection = await db.GmailConnections.SingleOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            ?? throw new GmailNotConnectedException();

        if (connection.Status == GmailConnectionStatus.Expired)
        {
            throw new GmailAuthExpiredException();
        }

        var refreshToken = tokenProtector.TryUnprotect(connection.EncryptedRefreshToken);
        if (refreshToken is null)
        {
            await MarkExpiredAsync(connection, cancellationToken);
            throw new GmailAuthExpiredException("Stored Gmail credentials could not be decrypted.");
        }

        var settings = options.Value;
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId ?? string.Empty,
            ["client_secret"] = settings.ClientSecret ?? string.Empty,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }), cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // invalid_grant: revoked by the user, password change, or 6 months unused.
            LogTokenRejected(logger, userId, (int)response.StatusCode);
            await MarkExpiredAsync(connection, cancellationToken);
            throw new GmailAuthExpiredException();
        }

        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new HttpRequestException("Empty token response from Google.");

        cache.Set(cacheKey, token.AccessToken, TimeSpan.FromSeconds(Math.Max(60, token.ExpiresIn - 120)));
        return token.AccessToken;
    }

    /// <summary>Revokes the grant at Google (best effort) and deletes it locally.</summary>
    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connection = await db.GmailConnections.SingleOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (connection is null)
        {
            return;
        }

        var refreshToken = tokenProtector.TryUnprotect(connection.EncryptedRefreshToken);
        if (refreshToken is not null)
        {
            try
            {
                using var response = await http.PostAsync(RevokeEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    LogRevokeFailed(logger, userId, (int)response.StatusCode);
                }
            }
            catch (HttpRequestException ex)
            {
                LogRevokeError(logger, ex, userId);
            }
        }

        cache.Remove($"gmail-access:{userId}");
        db.GmailConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkExpiredAsync(GmailConnection connection, CancellationToken cancellationToken)
    {
        connection.Status = GmailConnectionStatus.Expired;
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    [LoggerMessage(LogLevel.Warning, "Google rejected the refresh token for user {UserId} (HTTP {Status}); connection marked expired")]
    private static partial void LogTokenRejected(ILogger logger, Guid userId, int status);

    [LoggerMessage(LogLevel.Warning, "Revoking Google token for user {UserId} returned HTTP {Status}")]
    private static partial void LogRevokeFailed(ILogger logger, Guid userId, int status);

    [LoggerMessage(LogLevel.Warning, "Revoking Google token for user {UserId} failed")]
    private static partial void LogRevokeError(ILogger logger, Exception exception, Guid userId);
}
