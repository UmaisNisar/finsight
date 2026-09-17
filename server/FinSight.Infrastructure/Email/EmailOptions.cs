namespace FinSight.Infrastructure.Email;

public enum SmtpSecurity
{
    /// <summary>Connect in plain text, then upgrade with STARTTLS (required). The usual choice on port 587.</summary>
    StartTls,

    /// <summary>TLS from the first byte. The usual choice on port 465.</summary>
    SslOnConnect,

    /// <summary>No encryption. Only allowed for a relay on the same machine (localhost).</summary>
    None,
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }

    /// <summary>Secret: set it with user secrets or an environment variable (Email__Smtp__Password), never in appsettings.</summary>
    public string? Password { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    public string? From { get; set; }
    public string FromName { get; set; } = "FinSight";
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>
    /// Writes messages as .eml files instead of sending them. Used automatically in Development (".data/mail") when no SMTP
    /// host is set, so email can be checked locally without a mail server.
    /// </summary>
    public string? PickupDirectory { get; set; }

    /// <summary>How many times a month's summary is attempted before giving up on that month.</summary>
    public int DigestMaxAttempts { get; set; } = 3;
}

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Where users open FinSight, for links in email (for example https://finsight.example.com). No trailing slash needed.</summary>
    public string? PublicUrl { get; set; }
}
