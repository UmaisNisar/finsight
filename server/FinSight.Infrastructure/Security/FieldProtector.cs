using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace FinSight.Infrastructure.Security;

/// <summary>Encrypts individual values at rest (OAuth tokens, transaction descriptions).</summary>
public interface IFieldProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}

public interface ITokenProtector
{
    string Protect(string token);

    /// <returns>The token, or null if it can no longer be decrypted (for example after key loss).</returns>
    string? TryUnprotect(string ciphertext);
}

/// <summary>Protects users' own AI provider keys. A separate purpose from OAuth tokens and data fields.</summary>
public interface IApiKeyProtector
{
    string Protect(string apiKey);

    /// <returns>The key, or null if it can no longer be decrypted (for example after key loss).</returns>
    string? TryUnprotect(string ciphertext);
}

/// <summary>
/// ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256, automatic key rotation). Separate
/// purposes isolate tokens from other fields: a key compromise of one purpose does not expose the other.
/// </summary>
internal sealed class DataProtectionFieldProtector(IDataProtectionProvider provider) : IFieldProtector, ITokenProtector, IApiKeyProtector
{
    private const string Marker = "enc:";
    private readonly IDataProtector _fields = provider.CreateProtector("FinSight.Fields.v1");
    private readonly IDataProtector _tokens = provider.CreateProtector("FinSight.OAuthTokens.v1");
    private readonly IDataProtector _apiKeys = provider.CreateProtector("FinSight.GeminiApiKey.v1");

    public string Protect(string plaintext) => Marker + _fields.Protect(plaintext);

    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith(Marker, StringComparison.Ordinal))
        {
            return ciphertext;
        }

        try
        {
            return _fields.Unprotect(ciphertext[Marker.Length..]);
        }
        catch (CryptographicException)
        {
            return "[unavailable]";
        }
    }

    string ITokenProtector.Protect(string token) => _tokens.Protect(token);

    public string? TryUnprotect(string ciphertext)
    {
        try
        {
            return _tokens.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    string IApiKeyProtector.Protect(string apiKey) => _apiKeys.Protect(apiKey);

    string? IApiKeyProtector.TryUnprotect(string ciphertext)
    {
        try
        {
            return _apiKeys.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
