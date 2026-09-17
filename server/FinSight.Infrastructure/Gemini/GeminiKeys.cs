using System.Net;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Gemini;

/// <summary>Which Gemini API key a unit of work (request or background job) uses.</summary>
public interface IGeminiKeyResolver
{
    /// <returns>The current user's own key if they saved one, otherwise the server key, otherwise null.</returns>
    Task<string?> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>The key with whose it is, for quotas: only calls on the server's key count towards the shared ceiling.</summary>
    async Task<GeminiKeyResolution?> ResolveDetailsAsync(CancellationToken cancellationToken) =>
        await ResolveAsync(cancellationToken) is { } key ? new GeminiKeyResolution(key, IsUserKey: false, UserId: null, Email: null) : null;
}

/// <param name="IsUserKey">True for the user's own saved key, false for the server's.</param>
public sealed record GeminiKeyResolution(string ApiKey, bool IsUserKey, Guid? UserId, string? Email);

/// <summary>
/// Resolves the key for whoever <see cref="IUserContext"/> says the scope acts for, so a background job uses the key of the
/// job's user. The decrypted key is cached for the scope only, never statically.
/// </summary>
public sealed class GeminiKeyResolver(FinSightDbContext db, IUserContext userContext, IApiKeyProtector protector, IOptions<GeminiOptions> options) : IGeminiKeyResolver
{
    private bool _resolved;
    private GeminiKeyResolution? _resolution;

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken) => (await ResolveDetailsAsync(cancellationToken))?.ApiKey;

    public async Task<GeminiKeyResolution?> ResolveDetailsAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _resolution;
        }

        string? userKey = null;
        string? email = null;
        var userId = userContext.UserId;
        if (userId is { } id)
        {
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new { u.EncryptedGeminiApiKey, u.Email })
                .SingleOrDefaultAsync(cancellationToken);

            // A key that can no longer be decrypted (lost key ring) behaves as if none were saved.
            userKey = user?.EncryptedGeminiApiKey is { } encrypted ? protector.TryUnprotect(encrypted) : null;
            email = user?.Email;
        }

        var serverKey = options.Value.IsConfigured ? options.Value.ApiKey : null;
        _resolution = !string.IsNullOrWhiteSpace(userKey) ? new GeminiKeyResolution(userKey, true, userId, email)
            : serverKey is not null ? new GeminiKeyResolution(serverKey, false, userId, email)
            : null;
        _resolved = true;
        return _resolution;
    }
}

public enum GeminiKeyCheck
{
    Valid,
    Rejected,

    /// <summary>The key's project doesn't have the Gemini API switched on, typically a key made in Google Cloud rather than AI Studio.</summary>
    ServiceDisabled,
    Unavailable,
}

/// <summary>Checks a Gemini API key with Google by listing one model. Only status codes are logged, never the key.</summary>
/// <remarks>Uses the response body to recognise a disabled API, so the headers-only read is not used.</remarks>
public sealed partial class GeminiKeyValidator(HttpClient http, ILogger<GeminiKeyValidator> logger)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1";

    public async Task<GeminiKeyCheck> CheckAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            // Listing models spends no quota. Rate limited still means Google recognised the key: it's just busy.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return GeminiKeyCheck.Valid;
            }

            LogCheckFailed(logger, (int)response.StatusCode);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The body is read only to tell a disabled API apart from a wrong key; it is never logged.
                return GeminiErrors.IsServiceDisabled(await GeminiErrors.ReadBodyAsync(response, cancellationToken))
                    ? GeminiKeyCheck.ServiceDisabled
                    : GeminiKeyCheck.Rejected;
            }

            return GeminiKeyCheck.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogCheckUnreachable(logger, "Timeout");
            return GeminiKeyCheck.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            LogCheckUnreachable(logger, ex.GetType().Name);
            return GeminiKeyCheck.Unavailable;
        }
    }

    [LoggerMessage(LogLevel.Information, "Gemini key check returned HTTP {Status}")]
    private static partial void LogCheckFailed(ILogger logger, int status);

    [LoggerMessage(LogLevel.Warning, "Gemini key check could not reach Google: {Failure}")]
    private static partial void LogCheckUnreachable(ILogger logger, string failure);
}
