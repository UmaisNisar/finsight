using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace FinSight.Api.Hosting;

public enum KeyRingProtection
{
    /// <summary>Keys are encrypted with the PKCS#12 certificate in DataProtection:CertificatePath.</summary>
    Certificate,

    /// <summary>Keys are encrypted with Windows DPAPI, tied to the Windows account running FinSight.</summary>
    Dpapi,

    /// <summary>Keys are written unencrypted. Allowed in Development, and elsewhere only with DataProtection:AllowUnprotectedKeys.</summary>
    None,
}

public static class DataProtectionSetup
{
    public static KeyRingProtection AddFinSightDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var keysPath = configuration["DataProtection:KeysPath"] ?? ".data/keys";
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("FinSight")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath, environment.ContentRootPath)));

        // The key ring decrypts every encrypted field, so it must not sit next to the database in plain form. With a custom key
        // directory, Data Protection writes keys unencrypted unless told otherwise: use a certificate when one is configured,
        // else DPAPI on Windows. Anywhere else (Linux containers) startup fails outside Development unless the operator
        // explicitly accepts an unencrypted key ring.
        var protection = ResolveKeyRingProtection(configuration, environment, OperatingSystem.IsWindows());
        if (protection == KeyRingProtection.Certificate)
        {
            var keyCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                Path.GetFullPath(configuration["DataProtection:CertificatePath"]!, environment.ContentRootPath), configuration["DataProtection:CertificatePassword"]);
            dataProtection.ProtectKeysWithCertificate(keyCertificate).UnprotectKeysWithAnyCertificate(keyCertificate);
        }
        else if (protection == KeyRingProtection.Dpapi && OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi();
        }

        return protection;
    }

    /// <summary>
    /// Decides how the key ring is protected. Throws when the keys would be stored unencrypted outside Development and
    /// DataProtection:AllowUnprotectedKeys isn't set, so a production deployment can't silently run with a plain key ring.
    /// </summary>
    public static KeyRingProtection ResolveKeyRingProtection(IConfiguration configuration, IHostEnvironment environment, bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(configuration["DataProtection:CertificatePath"]))
        {
            return KeyRingProtection.Certificate;
        }

        if (isWindows)
        {
            return KeyRingProtection.Dpapi;
        }

        if (environment.IsDevelopment() || configuration.GetValue<bool>("DataProtection:AllowUnprotectedKeys"))
        {
            return KeyRingProtection.None;
        }

        throw new InvalidOperationException(
            "The Data Protection key ring would be stored unencrypted. Set DataProtection:CertificatePath and " +
            "DataProtection:CertificatePassword to a PKCS#12 certificate that encrypts it, or set " +
            "DataProtection:AllowUnprotectedKeys=true to accept an unencrypted key ring (local testing only).");
    }
}
