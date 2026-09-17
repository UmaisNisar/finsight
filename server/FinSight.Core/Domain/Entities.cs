namespace FinSight.Core.Domain;

/// <summary>Marker for rows that belong to exactly one user. Ownership is enforced on every query.</summary>
public interface IUserOwned
{
    Guid UserId { get; }
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Google's stable "sub" claim. Null for demo users.</summary>
    public string? GoogleSubject { get; set; }

    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the user finished (or skipped past) first-run onboarding. Null sends the web app to onboarding.</summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; }

    /// <summary>The user's own Gemini API key, encrypted with ASP.NET Data Protection. Never leaves the server.</summary>
    public string? EncryptedGeminiApiKey { get; set; }

    /// <summary>Last four characters of the Gemini key, so the UI can show which key is saved. Not secret.</summary>
    public string? GeminiApiKeyHint { get; set; }

    public UserSettings Settings { get; set; } = new();

    // Automation state, written by the scheduler with compare-and-set updates so that two server instances never scan
    // or email the same user at the same time.

    /// <summary>When the next automatic Gmail scan is due. Null until the scheduler assigns the user's daily slot.</summary>
    public DateTimeOffset? NextAutoScanAt { get; set; }

    /// <summary>When automatic import was turned on. Only statements discovered after this are imported automatically.</summary>
    public DateTimeOffset? AutoImportEnabledAt { get; set; }

    /// <summary>The month ("yyyy-MM") of the last monthly summary email, so a month is never emailed twice.</summary>
    public string? LastDigestSentFor { get; set; }

    /// <summary>The earliest time the monthly summary may be tried again. Also a short lease while one is being sent.</summary>
    public DateTimeOffset? DigestNextAttemptAt { get; set; }

    /// <summary>The month ("yyyy-MM") that <see cref="DigestFailures"/> counts delivery failures for.</summary>
    public string? DigestFailedFor { get; set; }

    public int DigestFailures { get; set; }
}

public sealed class UserSettings
{
    public string Currency { get; set; } = "CAD";
    public string DateFormat { get; set; } = "MMM d, yyyy";
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool AiCategorizationEnabled { get; set; } = true;
    public bool AiInsightsEnabled { get; set; } = true;

    /// <summary>Scan Gmail for new statements once a day. Scans only discover; nothing is imported unless <see cref="AutoImportEnabled"/>.</summary>
    public bool AutoScanEnabled { get; set; }

    /// <summary>Import newly found statements for a bank and account (last four digits) the user has imported before. Needs auto scan.</summary>
    public bool AutoImportEnabled { get; set; }

    /// <summary>Email a summary of the previous month early each month.</summary>
    public bool MonthlyDigestEnabled { get; set; }
}

/// <summary>
/// A signed-in browser session. The session cookie names it; signing out deletes the row, so a copy of the cookie stops
/// working even though the cookie itself has not expired.
/// </summary>
public sealed class UserSession : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GmailConnection : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string GoogleEmail { get; set; }

    /// <summary>Refresh token encrypted with ASP.NET Data Protection. Never leaves the server.</summary>
    public required string EncryptedRefreshToken { get; set; }

    public required string Scopes { get; set; }
    public GmailConnectionStatus Status { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
}

public sealed class Statement : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    public StatementSourceKind Source { get; set; }

    /// <summary>
    /// Idempotency key, unique per user: <c>gmail:{messageId}:{partId}</c>, <c>gmail:{messageId}:alert</c> for an
    /// email that announces a statement without attaching it, or <c>upload:{sha256}</c>.
    /// Gmail attachment ids are not stable between API calls, so the MIME part id is used instead.
    /// </summary>
    public required string SourceKey { get; set; }

    public string? SourceMessageId { get; set; }
    public string? SourceThreadId { get; set; }
    public string? SourcePartId { get; set; }
    public string? Subject { get; set; }
    public string? Sender { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }

    public required string Filename { get; set; }
    public long? SizeBytes { get; set; }

    /// <summary>SHA-256 of the file's bytes. The file itself is not stored.</summary>
    public string? ContentHash { get; set; }

    /// <summary>What the statement was last imported from. Null until a file has been read (and for demo data).</summary>
    public StatementFileFormat? Format { get; set; }

    public DocumentKind DocumentKind { get; set; }
    public double DetectionConfidence { get; set; }

    /// <summary>JSON array of short reasons the detector gave for its confidence.</summary>
    public string? DetectionReasons { get; set; }

    public string? Institution { get; set; }
    public AccountType AccountType { get; set; }

    /// <summary>Last four digits only, e.g. "4821". Full account numbers are never stored.</summary>
    public string? AccountMask { get; set; }

    public string? Currency { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }

    public StatementStatus Status { get; set; }

    /// <summary>Stable failure code (see <c>StatementFailure</c>); the UI maps it to human copy.</summary>
    public string? FailureCode { get; set; }

    public double? ExtractionConfidence { get; set; }

    /// <summary>JSON array of human-readable extraction warnings.</summary>
    public string? ExtractionWarnings { get; set; }

    public int TransactionCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    public List<Transaction> Transactions { get; set; } = [];
}

public sealed class Transaction : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid StatementId { get; set; }
    public Statement? Statement { get; set; }

    /// <summary>Stable identity across re-imports: account + date + amount + description (or the bank's transaction id) + occurrence.</summary>
    public required string Fingerprint { get; set; }

    public DateOnly Date { get; set; }
    public DateOnly? PostingDate { get; set; }

    /// <summary>Statement description with card/account numbers masked. Encrypted at rest.</summary>
    public required string Description { get; set; }

    /// <summary>Signed from the account holder's perspective: positive = money in, negative = money out.</summary>
    public decimal Amount { get; set; }

    public decimal? Balance { get; set; }
    public required string Currency { get; set; }
    public double ExtractionConfidence { get; set; }

    // Values produced by the pipeline. Re-computed on every reprocess.
    public required string Merchant { get; set; }
    public required string MerchantKey { get; set; }
    public TransactionType Type { get; set; }
    public required string CategoryId { get; set; }
    public CategorySource CategorySource { get; set; }
    public double CategoryConfidence { get; set; }
    public bool IsRefund { get; set; }
    public bool IsReversal { get; set; }
    public Guid? TransferPairId { get; set; }

    // User overrides. Preserved across reprocessing by fingerprint; they win over pipeline values.
    public string? UserCategoryId { get; set; }
    public string? UserMerchant { get; set; }
    public TransactionType? UserType { get; set; }
    public bool IsExcluded { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string EffectiveCategoryId => UserCategoryId ?? CategoryId;
    public string EffectiveMerchant => UserMerchant ?? Merchant;
    public TransactionType EffectiveType => UserType ?? Type;
}

/// <summary>
/// Per-user merchant knowledge: explicit user choices ("always categorize X as Y") and cached
/// AI categorizations, so the same merchant is never sent to Gemini twice.
/// </summary>
public sealed class MerchantRule : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string MerchantKey { get; set; }
    public string? DisplayName { get; set; }
    public required string CategoryId { get; set; }
    public TransactionType? Type { get; set; }
    public CategorySource Source { get; set; }
    public double Confidence { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CustomCategory : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>Category id, always prefixed "custom." so it cannot collide with built-ins.</summary>
    public required string CategoryId { get; set; }

    public required string Name { get; set; }
    public required string GroupId { get; set; }
}

public sealed class FinancialAnalysisRecord : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>Hash of the facts sent to Gemini; a mismatch means the analysis is stale.</summary>
    public required string FactsHash { get; set; }

    public required string Model { get; set; }
    public required string ResultJson { get; set; }
    public required string CorrectionsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProcessingJob : IUserOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; }

    /// <summary>JSON array of <see cref="JobStep"/>; the UI renders it as a progress checklist.</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>Stable error code for the UI, never an exception message.</summary>
    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <param name="Code">For a failed statement step, its stable failure code (see <c>StatementFailure</c>), so the UI can offer a fix.</param>
public sealed record JobStep(string Key, string Label, StepStatus Status, string? Detail = null, string? Code = null);
