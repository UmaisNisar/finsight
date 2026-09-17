using System.Text.Json;
using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Core.Statements;
using FinSight.Core.Text;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Pipeline;

namespace FinSight.Api.Contracts;

public sealed record Capabilities(bool GoogleSignIn, bool Gmail, bool Ai, bool Demo);

/// <param name="OnboardingCompleted">False sends the web app to first-run onboarding. Always true for demo users.</param>
public sealed record SessionUser(Guid Id, string Name, string Email, bool IsDemo, bool OnboardingCompleted);

public sealed record SessionResponse(bool Authenticated, SessionUser? User, Capabilities Capabilities);

/// <summary>Development-only OAuth setup report. Never contains credential values.</summary>
public sealed record AuthDiagnostics(GoogleDiagnostics Google, IReadOnlyList<string> Hints);

public sealed record GoogleDiagnostics(
    bool ClientIdSet,
    bool ClientIdFormatValid,
    bool ClientSecretSet,
    bool HandlerRegistered,
    bool RestartRequired,
    string JavaScriptOrigin,
    string RedirectUri,
    IReadOnlyList<string> Scopes);

/// <param name="EmailConfigured">Read-only: whether this server can send email. Monthly summaries are unavailable without it.</param>
public sealed record SettingsDto(
    string Currency,
    string DateFormat,
    ThemePreference Theme,
    bool AiCategorizationEnabled,
    bool AiInsightsEnabled,
    bool AutoScanEnabled,
    bool AutoImportEnabled,
    bool MonthlyDigestEnabled,
    bool EmailConfigured);

/// <summary>
/// Saves settings. The automation switches are optional so an older client that doesn't send them leaves them as they are;
/// read-only fields of <see cref="SettingsDto"/> are ignored.
/// </summary>
public sealed record UpdateSettingsRequest(
    string Currency,
    string DateFormat,
    ThemePreference Theme,
    bool AiCategorizationEnabled,
    bool AiInsightsEnabled,
    bool? AutoScanEnabled = null,
    bool? AutoImportEnabled = null,
    bool? MonthlyDigestEnabled = null);

public sealed record TestEmailResponse(bool Sent);

/// <summary>Which Gemini key AI features use. The key itself is never returned; <c>Hint</c> is "…" plus its last four characters.</summary>
/// <param name="Model">The primary model. Fallback models may answer when it's used up or failing.</param>
/// <param name="LastFailure">
/// <c>key_refused</c> when Google last refused the key in use, <c>quota_exhausted</c> when every model's quota was used up,
/// otherwise null. Cleared by the next successful call or by saving or removing a key. Kept in memory, so a restart clears it.
/// </param>
public sealed record AiKeyStatusDto(bool HasUserKey, string? Hint, bool ServerKeyAvailable, string Model, string? LastFailure = null, DateTimeOffset? LastFailureAt = null);

public sealed record SaveAiKeyRequest(string? ApiKey);

public sealed record GmailConnectionDto(bool Connected, string? Email, GmailConnectionStatus? Status, DateTimeOffset? ConnectedAt, DateTimeOffset? LastSyncedAt);

public sealed record JobStartedResponse(Guid JobId);

public sealed record UploadStartedResponse(Guid JobId, Guid StatementId);

public sealed record JobDto(Guid Id, JobKind Kind, JobStatus Status, IReadOnlyList<JobStep> Steps, string? ErrorCode, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record StatementDto(
    Guid Id,
    string Title,
    string? Institution,
    AccountType AccountType,
    string? AccountMask,
    DocumentKind DocumentKind,
    StatementSourceKind Source,
    string Filename,
    string? SenderName,
    DateTimeOffset? ReceivedAt,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    StatementStatus Status,
    string? FailureCode,
    string? FailureMessage,
    double DetectionConfidence,
    double? ExtractionConfidence,
    int TransactionCount,
    bool CanReprocess,
    bool ReprocessNeedsUpload,
    string? Subject,
    IReadOnlyList<string> DetectionReasons,
    string? SignInUrl,
    string? DownloadHint,
    StatementFileFormat? Format = null);

/// <summary>A bank FinSight can name, with trusted sign-in guidance from <c>KnownInstitutions</c> (never from user or email content).</summary>
public sealed record InstitutionDto(string Id, string Name, string? SignInUrl, string? DownloadHint);

public sealed record StatementDetailDto(
    StatementDto Statement,
    string? Subject,
    string? Currency,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    IReadOnlyList<string> DetectionReasons,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TransactionDto> Transactions);

public sealed record AccountRef(string? Institution, AccountType AccountType, string? Mask);

public sealed record TransactionDto(
    Guid Id,
    Guid StatementId,
    DateOnly Date,
    DateOnly? PostingDate,
    string Description,
    string Merchant,
    decimal Amount,
    string Currency,
    TransactionType Type,
    string CategoryId,
    string CategoryName,
    string GroupId,
    CategorySource CategorySource,
    double CategoryConfidence,
    bool IsRefund,
    bool IsReversal,
    bool IsExcluded,
    bool IsEdited,
    AccountRef Account);

public sealed record TransactionPage(IReadOnlyList<TransactionDto> Items, int Total, int Page, int PageSize, decimal MoneyIn, decimal MoneyOut);

public sealed record UpdateTransactionRequest(string? CategoryId, string? Merchant, TransactionType? Type, bool? IsExcluded, bool ApplyToMerchant = false, bool ResetOverrides = false);

public sealed record CategoryDto(string Id, string Name, string GroupId, CategoryKind Kind, bool IsCustom);

public sealed record CategoryGroupDto(string Id, string Name, IReadOnlyList<CategoryDto> Categories);

public sealed record CreateCategoryRequest(string Name, string GroupId);

public sealed record PeriodDto(string Preset, DateOnly Start, DateOnly End, string Label);

public sealed record SummaryResponse(
    PeriodDto Period,
    string Currency,
    FinancialSummary Summary,
    IReadOnlyList<RecurringDto> Recurring,
    IReadOnlyList<AnomalyDto> Anomalies,
    DateOnly? LatestTransactionDate,
    bool HasAnyData);

public sealed record AnomalyDto(Guid TransactionId, DateOnly Date, string Merchant, string CategoryId, string CategoryName, decimal Amount, AnomalyKind Kind, decimal? TypicalAmount);

public enum RecurringKindDto
{
    Subscription,
    Bill,
    Membership,
    Loan,
    Habit,
    Income,
    Other,
}

public sealed record RecurringDto(
    string MerchantKey,
    string Merchant,
    string CategoryId,
    string CategoryName,
    RecurringKindDto Kind,
    bool KindFromAi,
    RecurrenceFrequency Frequency,
    decimal Amount,
    decimal MonthlyEquivalent,
    bool AmountVaries,
    int Occurrences,
    DateOnly FirstDate,
    DateOnly LastDate,
    DateOnly NextExpectedDate,
    bool IsActive,
    double Confidence,
    bool IsIncome);

public sealed record RecurringResponse(IReadOnlyList<RecurringDto> Items, decimal MonthlyTotal, decimal MonthlySubscriptions, decimal AnnualTotal, bool AiReviewed, string Currency);

/// <param name="Configured">A Gemini key is available (the user's own or the server's). Without one, analyses are written by FinSight.</param>
/// <param name="Blocked">
/// Why asking Gemini right now would not help even though a key is configured: <c>key_refused</c> (Google refused the key last
/// time) or <c>limit_reached</c> (today's allowance is used up). Null when a try could succeed.
/// </param>
public sealed record AnalysisAvailability(bool Enabled, bool Configured, string? Blocked = null);

/// <param name="Model">The Gemini model that wrote the analysis, which may be a fallback model. Null for built-in analyses.</param>
/// <param name="Source"><c>ai</c> or <c>builtIn</c> (written by FinSight without AI). Null when there is no analysis.</param>
/// <param name="FallbackReason">
/// For built-in analyses, why AI wasn't used: <c>not_configured</c>, <c>key_refused</c>, <c>quota_exhausted</c>, <c>limit_reached</c>
/// or <c>unavailable</c>.
/// </param>
public sealed record AnalysisResponse(
    PeriodDto Period,
    AnalysisState State,
    FinancialAnalysis? Analysis,
    IReadOnlyList<AnalysisCorrection> Corrections,
    string? Model,
    DateTimeOffset? GeneratedAt,
    AnalysisAvailability Availability,
    string Currency,
    AnalysisSource? Source = null,
    string? FallbackReason = null);

public sealed record ProcessStatementsRequest(IReadOnlyList<Guid> StatementIds);

public sealed record DeleteResult(int Deleted);

public static class Mapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SettingsDto ToDto(this UserSettings s, bool emailConfigured) =>
        new(s.Currency, s.DateFormat, s.Theme, s.AiCategorizationEnabled, s.AiInsightsEnabled, s.AutoScanEnabled, s.AutoImportEnabled, s.MonthlyDigestEnabled, emailConfigured);

    public static StatementDto ToDto(this Statement s)
    {
        // Sign-in links come only from the hard-coded institution list, never from the email, so they can't be phishing links.
        var institution = KnownInstitutions.FindByName(s.Institution);
        return new(
            s.Id,
            StatementLabels.Title(s),
            s.Institution,
            s.AccountType,
            s.AccountMask,
            s.DocumentKind,
            s.Source,
            s.Filename,
            SenderName(s.Sender),
            s.ReceivedAt,
            s.PeriodStart,
            s.PeriodEnd,
            s.Status,
            s.FailureCode,
            s.FailureCode is null ? null : StatementFailure.Message(s.FailureCode),
            s.DetectionConfidence,
            s.ExtractionConfidence,
            s.TransactionCount,
            CanReprocess: s.Source != StatementSourceKind.Demo && s.Status is not (StatementStatus.Downloading or StatementStatus.Processing),
            ReprocessNeedsUpload: s.Source == StatementSourceKind.ManualUpload || StatementAlert.IsAlert(s),
            Subject: MaskedSubject(s.Subject),
            DetectionReasons: ReadList(s.DetectionReasons),
            SignInUrl: institution?.SignInUrl,
            DownloadHint: institution?.DownloadHint,
            Format: s.Format);
    }

    public static InstitutionDto ToDto(this Institution i) => new(i.Id, i.Name, i.SignInUrl, i.DownloadHint);

    /// <summary>Subjects are masked when discovered; masking again (idempotent) guards rows stored by older versions.</summary>
    public static string? MaskedSubject(string? subject) => subject is null ? null : SensitiveDataMasker.Mask(subject);

    public static IReadOnlyList<string> ReadList(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];

    public static TransactionDto ToDto(this Transaction t, Func<string, CategoryDefinition> resolve)
    {
        var category = resolve(t.EffectiveCategoryId);
        return new TransactionDto(
            t.Id,
            t.StatementId,
            t.Date,
            t.PostingDate,
            t.Description,
            t.EffectiveMerchant,
            t.Amount,
            t.Currency,
            t.EffectiveType,
            category.Id,
            category.Name,
            category.GroupId,
            t.UserCategoryId is not null ? CategorySource.User : t.CategorySource,
            t.UserCategoryId is not null ? 1 : t.CategoryConfidence,
            t.IsRefund,
            t.IsReversal,
            t.IsExcluded,
            t.UserCategoryId is not null || t.UserMerchant is not null || t.UserType is not null || t.IsExcluded,
            new AccountRef(t.Statement?.Institution, t.Statement?.AccountType ?? AccountType.Unknown, t.Statement?.AccountMask));
    }

    /// <summary>Display name only ("TD Canada Trust"), without the email address.</summary>
    private static string? SenderName(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        var bracket = from.IndexOf('<', StringComparison.Ordinal);
        var name = (bracket > 0 ? from[..bracket] : from).Trim().Trim('"');
        return name.Contains('@', StringComparison.Ordinal) ? name[(name.IndexOf('@', StringComparison.Ordinal) + 1)..] : name;
    }
}
