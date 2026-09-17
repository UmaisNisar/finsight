using FinSight.Core.Domain;

namespace FinSight.Core.Parsing;

public sealed record StatementParseContext(
    string DefaultCurrency,
    DateOnly ReferenceDate,
    string? SenderAddress = null,
    string? Subject = null);

/// <param name="Amount">Signed from the account holder's perspective: positive = money in.</param>
/// <param name="ExternalId">
/// The bank's own id for the transaction (OFX FITID), when the file has one. Used only to fingerprint the transaction, so
/// re-imports and overlapping downloads dedupe exactly; it is never stored.
/// </param>
public sealed record ParsedTransaction(
    DateOnly Date,
    DateOnly? PostingDate,
    string Description,
    decimal Amount,
    decimal? Balance,
    double Confidence,
    int Page,
    string? ExternalId = null);

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

    /// <summary>A structured file (CSV, OFX) whose layout couldn't be mapped with confidence. Nothing is guessed.</summary>
    Unrecognized,

    /// <summary>The file holds transactions for more than one account.</summary>
    MultipleAccounts,

    /// <summary>The file exceeded a parsing limit (rows or time).</summary>
    TooLarge,
}

public sealed record ParsedStatement(
    StatementMetadata Metadata,
    IReadOnlyList<ParsedTransaction> Transactions,
    IReadOnlyList<string> Warnings,
    double Confidence,
    bool Reconciled,
    ParseFailure Failure);
