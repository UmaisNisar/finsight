using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Import.Csv;
using FinSight.Core.Import.Ofx;
using FinSight.Core.Parsing;

namespace FinSight.Core.Import;

/// <summary>
/// Reads one kind of structured statement file (CSV, OFX) into the same <see cref="ParsedStatement"/> the PDF parser
/// produces, so every format goes through the same normalization, duplicate detection and categorization.
/// </summary>
/// <remarks>
/// Implementations are pure and bounded: at most <see cref="StructuredImportLimits.MaxTransactions"/> rows, and parsing stops
/// after <see cref="StructuredImportLimits.Timeout"/>. A file that can't be mapped with confidence fails with
/// <see cref="ParseFailure.Unrecognized"/> rather than being guessed. Account numbers are reduced to their last four digits
/// as soon as they are read, and descriptions are masked.
/// </remarks>
public interface IStatementFileParser
{
    ParsedStatement Parse(ReadOnlyMemory<byte> content, StatementParseContext context, CancellationToken cancellationToken = default);
}

public sealed record StructuredImportLimits(int MaxTransactions, TimeSpan Timeout)
{
    public static readonly StructuredImportLimits Default = new(20_000, TimeSpan.FromSeconds(15));
}

/// <summary>Picks the parser for a detected format. PDFs are read by the PDF text extractor and <see cref="StatementParser"/> instead.</summary>
public static class StatementFileParsers
{
    private static readonly CsvStatementParser Csv = new(StructuredImportLimits.Default);
    private static readonly OfxStatementParser Ofx = new(StructuredImportLimits.Default);

    public static IStatementFileParser? For(StatementFileFormat format) => format switch
    {
        StatementFileFormat.Csv => Csv,
        StatementFileFormat.Ofx or StatementFileFormat.Qfx => Ofx,
        _ => null,
    };

    /// <summary>"CSV", "OFX", "QFX" or "PDF".</summary>
    public static string DisplayName(StatementFileFormat format) => format.ToString().ToUpperInvariant();
}

/// <summary>Thrown inside a parser when a limit is hit; parsers turn it into <see cref="ParseFailure.TooLarge"/>.</summary>
internal sealed class ParseLimitExceededException : Exception
{
    public ParseLimitExceededException() : base("The file exceeded a parsing limit.") { }
}

/// <summary>Elapsed time and cancellation for one parse.</summary>
internal sealed class ParseClock(TimeSpan timeout, CancellationToken cancellationToken)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed > timeout)
        {
            throw new ParseLimitExceededException();
        }
    }
}

internal static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Decodes a downloaded text file: a byte order mark wins (UTF-8, UTF-16), then strict UTF-8, then Windows-1252, which is what
    /// older bank exports and OFX 1.x <c>CHARSET:1252</c> files use.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static bool HasUtf16Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
}

/// <summary>Shared by the structured parsers: builds the result and settles the sign convention of card files.</summary>
internal static partial class StructuredStatement
{
    public const double Confidence = 0.95;
    public const double ReconciledConfidence = 0.99;

    public const string ReversedSignsWarning = "This file lists card charges as positive amounts, so FinSight reversed the signs.";

    [GeneratedRegex(@"\b(PAYMENT|PYMT|PAIEMENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentWords();

    public static ParsedStatement Failed(StatementParseContext context, ParseFailure failure, string warning) =>
        new(new StatementMetadata(null, AccountType.Unknown, null, context.DefaultCurrency, null, null, null, null), [], [warning], 0, false, failure);

    /// <summary>
    /// Whether a card file lists charges as positive amounts. Payments to the card are money in, so when the file's payment rows
    /// are mostly negative, its charges are positive. Without payment rows, <paramref name="majorityDecides"/> lets the most common
    /// sign decide: most rows on a card are charges.
    /// </summary>
    public static bool ChargesArePositive(IEnumerable<(string Description, decimal Amount)> rows, bool majorityDecides)
    {
        var list = rows.Where(r => r.Amount != 0).ToList();
        var payments = list.Where(r => PaymentWords().IsMatch(r.Description)).ToList();
        var negativePayments = payments.Count(p => p.Amount < 0);
        var positivePayments = payments.Count - negativePayments;
        if (negativePayments != positivePayments)
        {
            return negativePayments > positivePayments;
        }

        if (!majorityDecides)
        {
            return false;
        }

        var positive = list.Count(r => r.Amount > 0);
        return positive > list.Count - positive;
    }

    public static ParsedStatement Build(
        IReadOnlyList<ParsedTransaction> transactions,
        string? institution,
        AccountType accountType,
        string? accountMask,
        string currency,
        DateOnly? periodStart,
        DateOnly? periodEnd,
        decimal? openingBalance,
        decimal? closingBalance,
        List<string> warnings)
    {
        if (transactions.Count == 0)
        {
            warnings.Add("The file has no transactions.");
            return new ParsedStatement(
                new StatementMetadata(institution, accountType, accountMask, currency, periodStart, periodEnd, openingBalance, closingBalance),
                [], warnings, 0.1, false, ParseFailure.NoTransactions);
        }

        var first = transactions.Min(t => t.Date);
        var last = transactions.Max(t => t.Date);
        periodStart = periodStart is { } start && start <= first ? start : first;
        periodEnd = periodEnd is { } end && end >= last ? end : last;

        var reconciled = openingBalance is { } opening && closingBalance is { } closing
            && opening + transactions.Sum(t => t.Amount) == closing;

        return new ParsedStatement(
            new StatementMetadata(institution, accountType, accountMask, currency, periodStart, periodEnd, openingBalance, closingBalance),
            transactions,
            warnings,
            reconciled ? ReconciledConfidence : Confidence,
            reconciled,
            ParseFailure.None);
    }
}
