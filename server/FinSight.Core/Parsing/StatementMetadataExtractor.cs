using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Core.Text;

namespace FinSight.Core.Parsing;

/// <summary>Reads statement-level facts (institution, account, currency, period, balances) from its text.</summary>
internal static partial class StatementMetadataExtractor
{
    private const string MonthWord = @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?";

    private const string DatePhrase =
        @"(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/.]\d{1,2}[/.]\d{2,4}|" + MonthWord + @"\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?|\d{1,2}\s+" + MonthWord + @"(?:,?\s+\d{4})?)";

    [GeneratedRegex(@"(?<a>" + DatePhrase + @")\s*(?:to|-|–|—|through|thru|au)\s*(?<b>" + DatePhrase + ")", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodRange();

    [GeneratedRegex(@"(?:statement date|closing date|period ending|statement closing date|as of|as at)\W{0,3}(?<d>" + DatePhrase + ")", RegexOptions.IgnoreCase)]
    private static partial Regex StatementDate();

    [GeneratedRegex(@"(?:account|acct|card)\s*(?:number|no\.?|#)?\s*(?:ending\s+in)?\s*[:#]?\s*(?<n>(?:[\dX\*•]{2,}[\s-]?){0,5}\d{3,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccountNumber();

    [GeneratedRegex(@"ending\s+in\s+(?<n>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndingIn();

    [GeneratedRegex(@"\b(OPENING|BEGINNING|STARTING|PREVIOUS( STATEMENT)?) BALANCE\b|\bBALANCE (BROUGHT )?FORWARD\b|\bBROUGHT FORWARD\b", RegexOptions.IgnoreCase)]
    internal static partial Regex OpeningBalanceLabel();

    [GeneratedRegex(@"\b(CLOSING|ENDING|NEW) BALANCE\b|\bBALANCE CARRIED FORWARD\b|\bCARRIED FORWARD\b", RegexOptions.IgnoreCase)]
    internal static partial Regex ClosingBalanceLabel();

    public static AccountType DetectAccountType(string text, string? subject)
    {
        var haystack = text + "\n" + subject;
        int Count(string pattern) => Regex.Count(haystack, pattern, RegexOptions.IgnoreCase);

        var card = Count(@"\bCREDIT CARD\b|\bMINIMUM PAYMENT\b|\bCREDIT LIMIT\b|\bAVAILABLE CREDIT\b|\bPAYMENT DUE\b|\bVISA\b|\bMASTERCARD\b|\bAMERICAN EXPRESS\b|\bCARD ?MEMBER\b|\bCARDHOLDER\b");
        var chequing = Count(@"\bCHEQUING\b|\bCHECKING\b|\bCURRENT ACCOUNT\b|\bWITHDRAWALS\b|\bDEPOSITS\b|\bPAID OUT\b|\bPAID IN\b");
        var savings = Count(@"\bSAVINGS ACCOUNT\b|\bHIGH INTEREST SAVINGS\b|\bSAVINGS\b");
        var lineOfCredit = Count(@"\bLINE OF CREDIT\b|\bHELOC\b");

        if (lineOfCredit >= 2)
        {
            return AccountType.LineOfCredit;
        }

        if (card >= 3 && card >= chequing)
        {
            return AccountType.CreditCard;
        }

        if (savings > chequing && savings >= 2)
        {
            return AccountType.Savings;
        }

        return chequing > 0 ? AccountType.Chequing : card >= 2 ? AccountType.CreditCard : AccountType.Unknown;
    }

    public static string? DetectInstitution(IReadOnlyList<TextLine> lines, string? senderAddress)
    {
        var header = string.Join('\n', lines.Where(l => l.Page == 1).Take(15).Select(l => l.Text));
        var institution = KnownInstitutions.FindInText(header)
            ?? (senderAddress is null ? null : KnownInstitutions.FindBySenderDomain(senderAddress))
            ?? KnownInstitutions.FindInText(string.Join('\n', lines.Where(l => l.Page == 1).Select(l => l.Text)));
        return institution?.Name;
    }

    public static string? DetectAccountMask(string text)
    {
        var ending = EndingIn().Match(text);
        if (ending.Success)
        {
            return ending.Groups["n"].Value;
        }

        foreach (Match match in AccountNumber().Matches(text))
        {
            var mask = SensitiveDataMasker.LastFourOf(match.Groups["n"].Value);
            if (mask is { Length: 4 })
            {
                return mask;
            }
        }

        return null;
    }

    public static string DetectCurrency(string text, string defaultCurrency)
    {
        var scores = new Dictionary<string, int>
        {
            ["CAD"] = Regex.Count(text, @"\bCAD\b|CA\$|\bCANADIAN DOLLARS?\b", RegexOptions.IgnoreCase),
            ["USD"] = Regex.Count(text, @"\bUSD\b|US\$|\bU\.S\. DOLLARS?\b", RegexOptions.IgnoreCase),
            ["EUR"] = Regex.Count(text, @"€|\bEUR\b|\bEUROS?\b", RegexOptions.IgnoreCase),
            ["GBP"] = Regex.Count(text, @"£|\bGBP\b|\bPOUNDS STERLING\b", RegexOptions.IgnoreCase),
        };

        // Foreign-currency purchases mention other currencies, so the user's default wins ties and
        // another currency must be mentioned more often to take over.
        var defaultMentions = scores.GetValueOrDefault(defaultCurrency);
        var best = scores.Where(kv => kv.Key != defaultCurrency).OrderByDescending(kv => kv.Value).First();
        return best.Value > defaultMentions ? best.Key : defaultCurrency;
    }

    public static (PartialDate? Start, PartialDate? End) DetectPeriod(IReadOnlyList<TextLine> lines)
    {
        var candidates = lines.Where(l => l.Page == 1).Concat(lines.Where(l => l.Page != 1).Take(40));

        foreach (var line in candidates)
        {
            var range = PeriodRange().Match(line.Text);
            if (range.Success
                && DateTokenParser.TryParseText(Clean(range.Groups["a"].Value), out var a)
                && DateTokenParser.TryParseText(Clean(range.Groups["b"].Value), out var b)
                && (a.Year is not null || b.Year is not null))
            {
                if (a.Year is null && b.Year is not null && !a.IsNumeric && !b.IsNumeric)
                {
                    a = a with { Year = a.First > b.First ? b.Year - 1 : b.Year };
                }

                return (a, b);
            }
        }

        foreach (var line in lines.Where(l => l.Page == 1))
        {
            var statementDate = StatementDate().Match(line.Text);
            if (statementDate.Success && DateTokenParser.TryParseText(Clean(statementDate.Groups["d"].Value), out var end) && end.Year is not null)
            {
                return (null, end);
            }
        }

        return (null, null);

        static string Clean(string value) =>
            Regex.Replace(value, @"(\d)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase).Trim();
    }
}
