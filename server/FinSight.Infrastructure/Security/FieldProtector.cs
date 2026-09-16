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

/// <summary>
/// ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256, automatic key rotation). Separate
/// purposes isolate tokens from other fields: a key compromise of one purpose does not expose the other.
/// </summary>
internal sealed class DataProtectionFieldProtector(IDataProtectionProvider provider) : IFieldProtector, ITokenProtector
{
    private const string Marker = "enc:";
    private readonly IDataProtector _fields = provider.CreateProtector("FinSight.Fields.v1");
    private readonly IDataProtector _tokens = provider.CreateProtector("FinSight.OAuthTokens.v1");

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
}
