using FinSight.Core.Domain;

namespace FinSight.Core.Parsing;

public sealed record StatementParseContext(
    string DefaultCurrency,
    DateOnly ReferenceDate,
    string? SenderAddress = null,
    string? Subject = null);

/// <param name="Amount">Signed from the account holder's perspective: positive = money in.</param>
public sealed record ParsedTransaction(
    DateOnly Date,
    DateOnly? PostingDate,
    string Description,
    decimal Amount,
    decimal? Balance,
    double Confidence,
    int Page);

public sealed record StatementMetadata(
    string? Institution,
    AccountType AccountType,
    string? AccountMask,
    string Currency,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal? OpeningBalance,
    decimal? ClosingBalance);

public enum ParseFailure
{
    None,

    /// <summary>The PDF has little or no text layer: probably scanned, needs OCR.</summary>
    NoTextLayer,

    /// <summary>Text was found but no rows looked like transactions.</summary>
    NoTransactions,
}

public sealed record ParsedStatement(
    StatementMetadata Metadata,
    IReadOnlyList<ParsedTransaction> Transactions,
    IReadOnlyList<string> Warnings,
    double Confidence,
    bool Reconciled,
    ParseFailure Failure);
