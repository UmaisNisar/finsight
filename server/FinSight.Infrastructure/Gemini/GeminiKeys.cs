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
}

/// <summary>
/// Resolves the key for whoever <see cref="IUserContext"/> says the scope acts for, so a background job uses the key of the
/// job's user. The decrypted key is cached for the scope only, never statically.
/// </summary>
public sealed class GeminiKeyResolver(FinSightDbContext db, IUserContext userContext, IApiKeyProtector protector, IOptions<GeminiOptions> options) : IGeminiKeyResolver
{
    private bool _resolved;
    private string? _key;

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _key;
        }

        string? userKey = null;
        if (userContext.UserId is { } userId)
        {
            var encrypted = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.EncryptedGeminiApiKey)
                .SingleOrDefaultAsync(cancellationToken);

            // A key that can no longer be decrypted (lost key ring) behaves as if none were saved.
            userKey = encrypted is null ? null : protector.TryUnprotect(encrypted);
        }

        var serverKey = options.Value.IsConfigured ? options.Value.ApiKey : null;
        _key = string.IsNullOrWhiteSpace(userKey) ? serverKey : userKey;
        _resolved = true;
        return _key;
    }
}

public enum GeminiKeyCheck
{
    Valid,
    Rejected,
    RateLimited,
    Unavailable,
}

/// <summary>Checks a Gemini API key with Google by listing one model. Only status codes are logged, never the key.</summary>
public sealed partial class GeminiKeyValidator(HttpClient http, ILogger<GeminiKeyValidator> logger)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1";

    public async Task<GeminiKeyCheck> CheckAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return GeminiKeyCheck.Valid;
            }

            LogCheckFailed(logger, (int)response.StatusCode);
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GeminiKeyCheck.Rejected,
                HttpStatusCode.TooManyRequests => GeminiKeyCheck.RateLimited,
                _ => GeminiKeyCheck.Unavailable,
            };
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
