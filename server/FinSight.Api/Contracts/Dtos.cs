using System.Text.Json;
using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Pipeline;

namespace FinSight.Api.Contracts;

public sealed record Capabilities(bool GoogleSignIn, bool Gmail, bool Ai, bool Demo);

public sealed record SessionUser(Guid Id, string Name, string Email, bool IsDemo);

public sealed record SessionResponse(bool Authenticated, SessionUser? User, Capabilities Capabilities);

public sealed record SettingsDto(
    string Currency,
    string DateFormat,
    ThemePreference Theme,
    bool AiCategorizationEnabled,
    bool AiInsightsEnabled,
    bool NotificationsEnabled);

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
    bool ReprocessNeedsUpload);

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

public sealed record AnalysisAvailability(bool Enabled, bool Configured);

public sealed record AnalysisResponse(
    PeriodDto Period,
    AnalysisState State,
    FinancialAnalysis? Analysis,
    IReadOnlyList<AnalysisCorrection> Corrections,
    string? Model,
    DateTimeOffset? GeneratedAt,
    AnalysisAvailability Availability,
    string Currency);

public sealed record ProcessStatementsRequest(IReadOnlyList<Guid> StatementIds);

public sealed record DeleteResult(int Deleted);

public static class Mapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SettingsDto ToDto(this UserSettings s) =>
        new(s.Currency, s.DateFormat, s.Theme, s.AiCategorizationEnabled, s.AiInsightsEnabled, s.NotificationsEnabled);

    public static StatementDto ToDto(this Statement s) => new(
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
        ReprocessNeedsUpload: s.Source == StatementSourceKind.ManualUpload);

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
