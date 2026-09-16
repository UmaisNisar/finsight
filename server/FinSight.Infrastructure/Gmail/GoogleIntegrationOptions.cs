namespace FinSight.Infrastructure.Gmail;

public sealed class GoogleIntegrationOptions
{
    public const string Section = "Google";

    public const string GmailReadonlyScope = "https://www.googleapis.com/auth/gmail.readonly";

    /// <summary>OAuth client id from Google Cloud Console. Set via user secrets or environment, never committed.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>How far back to search Gmail for statements.</summary>
    public int SearchMonths { get; set; } = 13;

    /// <summary>Upper bound on messages inspected per sync, to stay well inside Gmail API quotas.</summary>
    public int MaxMessagesPerSync { get; set; } = 250;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
