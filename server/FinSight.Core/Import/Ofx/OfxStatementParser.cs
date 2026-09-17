using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Parsing;
using FinSight.Core.Statements;
using FinSight.Core.Text;

namespace FinSight.Core.Import.Ofx;

/// <summary>
/// Reads OFX and QFX statement downloads: OFX 1.x (SGML, where value elements have no closing tag) and OFX 2.x (XML), for bank
/// accounts (<c>STMTRS</c>) and credit cards (<c>CCSTMTRS</c>).
/// </summary>
/// <remarks>
/// The file is read by a small tag scanner rather than an XML parser, so SGML and XML share one path and nothing in the file
/// (DTDs, entities, external references) is ever resolved. Account ids are reduced to their last four digits as they are
/// read. <c>TRNAMT</c> is signed from the account holder's view, as the OFX specification requires; card files that invert it
/// are detected from their payment rows and <c>TRNTYPE</c>, and corrected. <c>FITID</c> becomes the transaction's external id.
/// </remarks>
public sealed partial class OfxStatementParser(StructuredImportLimits limits) : IStatementFileParser
{
    public const string UnreadableWarning = "The file isn't a readable OFX statement.";

    [GeneratedRegex(@"^(\d{4})(\d{2})(\d{2})")]
    private static partial Regex DatePrefix();

    public ParsedStatement Parse(ReadOnlyMemory<byte> content, StatementParseContext context, CancellationToken cancellationToken = default)
    {
        var clock = new ParseClock(limits.Timeout, cancellationToken);
        clock.Check();
        try
        {
            var root = OfxReader.Read(TextDecoding.Decode(content.Span), (limits.MaxTransactions * 16) + 1_000, clock);
            return root is null ? Unreadable(context) : Map(root, context);
        }
        catch (ParseLimitExceededException)
        {
            return StructuredStatement.Failed(context, ParseFailure.TooLarge, $"The file has more than {limits.MaxTransactions:N0} transactions or took too long to read.");
        }
    }

    private ParsedStatement Map(OfxElement root, StatementParseContext context)
    {
        var statements = root.Descendants("STMTRS").Concat(root.Descendants("CCSTMTRS")).ToList();
        if (statements.Count == 0)
        {
            return Unreadable(context);
        }

        var accounts = statements.Select(s => new
        {
            Statement = s,
            IsCard = s.Name == "CCSTMTRS",
            Account = s.Child("BANKACCTFROM") ?? s.Child("CCACCTFROM"),
        }).ToList();

        // Masked as soon as it is read: the full account id is never kept.
        var masks = accounts.Select(a => SensitiveDataMasker.LastFourOf(a.Account?.Value("ACCTID"))).Distinct().ToList();
        if (masks.Count > 1 || accounts.Select(a => a.IsCard).Distinct().Count() > 1)
        {
            return StructuredStatement.Failed(context, ParseFailure.MultipleAccounts, "The file has transactions from more than one account.");
        }

        var first = accounts[0];
        var accountType = first.IsCard ? AccountType.CreditCard : AccountTypeOf(first.Account?.Value("ACCTTYPE"));
        var currencyCode = first.Statement.Value("CURDEF")?.ToUpperInvariant();
        var currency = currencyCode is { Length: 3 } && currencyCode.All(char.IsAsciiLetterUpper) ? currencyCode : context.DefaultCurrency;
        var organization = root.Descendants("FI").Select(fi => fi.Value("ORG")).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
        var institution = organization is null ? null : KnownInstitutions.FindInText(organization)?.Name;

        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? periodStart = null, periodEnd = null;
        decimal? closing = null;
        var bad = 0;

        foreach (var account in accounts)
        {
            var list = account.Statement.Child("BANKTRANLIST");
            periodStart = Earliest(periodStart, ReadDate(list?.Value("DTSTART")));
            periodEnd = Latest(periodEnd, ReadDate(list?.Value("DTEND")));
            if (ReadAmount(account.Statement.Child("LEDGERBAL")?.Value("BALAMT")) is { } ledger)
            {
                closing = ledger;
            }

            foreach (var item in list?.Children.Where(c => c.Name == "STMTTRN") ?? [])
            {
                var fitId = item.Value("FITID")?.Trim();
                if (ReadDate(item.Value("DTPOSTED")) is not { } date || ReadAmount(item.Value("TRNAMT")) is not { } amount)
                {
                    bad++;
                    continue;
                }

                // Overlapping statements inside one file repeat transactions; the bank's id says which.
                if (!string.IsNullOrEmpty(fitId) && !seen.Add(fitId))
                {
                    continue;
                }

                rows.Add(new Row(date, Description(item), amount, string.IsNullOrEmpty(fitId) ? null : fitId[..Math.Min(fitId.Length, 255)], item.Value("TRNTYPE")?.ToUpperInvariant()));
                if (rows.Count > limits.MaxTransactions)
                {
                    throw new ParseLimitExceededException();
                }
            }
        }

        if (bad > Math.Max(2, (rows.Count + bad) / 20))
        {
            return Unreadable(context);
        }

        var warnings = new List<string>();
        if (bad > 0)
        {
            warnings.Add($"{bad} transaction{(bad == 1 ? "" : "s")} couldn't be read and {(bad == 1 ? "was" : "were")} skipped.");
        }

        if (accountType == AccountType.CreditCard && ChargesArePositive(rows))
        {
            rows = rows.Select(r => r with { Amount = -r.Amount }).ToList();
            closing = -closing;
            warnings.Add(StructuredStatement.ReversedSignsWarning);
        }

        var transactions = rows
            .OrderBy(r => r.Date)
            .Select(r => new ParsedTransaction(r.Date, null, SensitiveDataMasker.Mask(r.Description), r.Amount, null, StructuredStatement.Confidence, 1, r.FitId))
            .ToList();

        return StructuredStatement.Build(transactions, institution, accountType, masks[0], currency, periodStart, periodEnd, null, closing, warnings);
    }

    /// <summary>
    /// A card file that follows the specification lists charges as negative. It's inverted when its payments are mostly negative,
    /// or when its DEBIT rows are mostly positive and its CREDIT rows mostly negative.
    /// </summary>
    private static bool ChargesArePositive(List<Row> rows)
    {
        if (StructuredStatement.ChargesArePositive(rows.Select(r => (r.Description, r.Amount)), majorityDecides: false))
        {
            return true;
        }

        var debits = rows.Where(r => r.Type == "DEBIT" && r.Amount != 0).ToList();
        var credits = rows.Where(r => r.Type == "CREDIT" && r.Amount != 0).ToList();
        return debits.Count > 0 && debits.Count(r => r.Amount > 0) * 2 > debits.Count
            && (credits.Count == 0 || credits.Count(r => r.Amount < 0) * 2 > credits.Count);
    }

    private static string Description(OfxElement transaction)
    {
        var name = Clean(transaction.Value("NAME") ?? transaction.Child("PAYEE")?.Value("NAME"));
        var memo = Clean(transaction.Value("MEMO"));
        if (name.Length == 0)
        {
            return memo.Length > 0 ? memo : Clean(transaction.Value("TRNTYPE")) is { Length: > 0 } type ? type : "Transaction";
        }

        if (memo.Length == 0 || name.Contains(memo, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        // Banks cut NAME at 32 characters and often repeat it in full in MEMO.
        return memo.Contains(name, StringComparison.OrdinalIgnoreCase) ? memo : $"{name} {memo}";
    }

    private static string Clean(string? value) => value is null ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static AccountType AccountTypeOf(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        "CHECKING" => AccountType.Chequing,
        "SAVINGS" or "MONEYMRKT" or "CD" => AccountType.Savings,
        "CREDITLINE" => AccountType.LineOfCredit,
        _ => AccountType.Unknown,
    };

    /// <summary>
    /// OFX dates are <c>YYYYMMDD[HHMMSS[.XXX]][[offset:TZ]]</c>, for example <c>20260815120000[-5:EST]</c>. Banks write the local date
    /// of the transaction, so the calendar date is taken as written and the time zone is not applied (applying it would move
    /// midnight postings to the previous day).
    /// </summary>
    internal static DateOnly? ReadDate(string? value)
    {
        var match = DatePrefix().Match(value?.Trim() ?? "");
        if (!match.Success)
        {
            return null;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return year is >= 1990 and <= 2100 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    private static decimal? ReadAmount(string? value)
    {
        var text = value?.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Some banks write a decimal comma.
        if (text.Contains(',', StringComparison.Ordinal) && !text.Contains('.', StringComparison.Ordinal))
        {
            text = text.Replace(',', '.');
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            && Math.Abs(amount) < 1_000_000_000_000m
            ? amount
            : null;
    }

    private static DateOnly? Earliest(DateOnly? current, DateOnly? next) => current is null ? next : next is null ? current : next < current ? next : current;

    private static DateOnly? Latest(DateOnly? current, DateOnly? next) => current is null ? next : next is null ? current : next > current ? next : current;

    private static ParsedStatement Unreadable(StatementParseContext context) =>
        StructuredStatement.Failed(context, ParseFailure.Unrecognized, UnreadableWarning);

    private sealed record Row(DateOnly Date, string Description, decimal Amount, string? FitId, string? Type);
}

internal sealed class OfxElement(string name)
{
    public string Name { get; } = name;

    /// <summary>The text of a value element; null for an aggregate.</summary>
    public string? Text { get; set; }

    public List<OfxElement> Children { get; } = [];

    public OfxElement? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public string? Value(string name) => Child(name)?.Text;

    public IEnumerable<OfxElement> Descendants(string name)
    {
        foreach (var child in Children)
        {
            if (child.Name == name)
            {
                yield return child;
            }

            foreach (var match in child.Descendants(name))
            {
                yield return match;
            }
        }
    }
}

/// <summary>
/// A tolerant tag scanner for OFX. Aggregates (elements that contain elements) are known by name; any other element is a value
/// whose text runs to the next tag, with or without a closing tag. Unknown closing tags are ignored.
/// </summary>
internal static class OfxReader
{
    private const int MaxDepth = 32;

    private static readonly HashSet<string> Aggregates = new(StringComparer.Ordinal)
    {
        "OFX", "SONRS", "STATUS", "FI", "STMTRS", "CCSTMTRS", "BANKACCTFROM", "BANKACCTTO", "CCACCTFROM", "CCACCTTO",
        "BANKTRANLIST", "STMTTRN", "LEDGERBAL", "AVAILBAL", "BALLIST", "BAL", "CURRENCY", "ORIGCURRENCY", "PAYEE",
        "EXTDPAYEE", "IMAGEDATA", "REWARDINFO", "INV401KBAL", "INVACCTFROM",
    };

    private static bool IsAggregate(string name) =>
        Aggregates.Contains(name) || name.EndsWith("MSGSRSV1", StringComparison.Ordinal) || name.EndsWith("MSGSRQV1", StringComparison.Ordinal)
        || name.EndsWith("TRNRS", StringComparison.Ordinal) || name.EndsWith("TRNRQ", StringComparison.Ordinal);

    /// <summary>The document under a synthetic root, or null when the file has no <c>&lt;OFX&gt;</c> element.</summary>
    public static OfxElement? Read(string text, int maxElements, ParseClock clock)
    {
        var start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var root = new OfxElement("#root");
        var stack = new List<OfxElement> { root };
        var count = 0;
        var i = start;

        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            if (string.CompareOrdinal(text, open, "<!--", 0, 4) == 0)
            {
                var endComment = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                i = endComment < 0 ? text.Length : endComment + 3;
                continue;
            }

            var close = text.IndexOf('>', open + 1);
            if (close < 0)
            {
                break;
            }

            var tag = text[(open + 1)..close].Trim();
            i = close + 1;
            if (tag.Length == 0 || tag[0] is '?' or '!')
            {
                continue;
            }

            if (++count > maxElements)
            {
                throw new ParseLimitExceededException();
            }

            if (count % 1024 == 0)
            {
                clock.Check();
            }

            if (tag[0] == '/')
            {
                var name = tag[1..].Trim().ToUpperInvariant();
                var index = stack.FindLastIndex(e => e.Name == name);
                if (index > 0)
                {
                    stack.RemoveRange(index, stack.Count - index);
                }

                continue;
            }

            var selfClosing = tag.EndsWith('/');
            var elementName = tag.TrimEnd('/').Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            var element = new OfxElement(elementName);
            stack[^1].Children.Add(element);

            if (selfClosing)
            {
                continue;
            }

            if (IsAggregate(elementName))
            {
                if (stack.Count > MaxDepth)
                {
                    return null;
                }

                stack.Add(element);
                continue;
            }

            var next = text.IndexOf('<', i);
            var raw = next < 0 ? text[i..] : text[i..next];
            element.Text = WebUtility.HtmlDecode(raw.Trim());
            i = next < 0 ? text.Length : next;
        }

        return root;
    }
}
