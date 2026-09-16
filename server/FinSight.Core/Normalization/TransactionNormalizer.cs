using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Parsing;

namespace FinSight.Core.Normalization;

public sealed record NormalizedTransaction(
    DateOnly Date,
    DateOnly? PostingDate,
    string Description,
    decimal Amount,
    decimal? Balance,
    string Currency,
    double ExtractionConfidence,
    string Fingerprint,
    MerchantName Merchant,
    bool IsReversal);

/// <summary>Converts parser output into the canonical transaction shape, independent of the bank.</summary>
public static partial class TransactionNormalizer
{
    [GeneratedRegex(@"\b(REVERSAL|REVERSED|VOID(ED)?|CANCEL(LED|ED|LATION)?|CORRECTION|ADJUSTMENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReversalWords();

    /// <summary>
    /// Identifies the account a statement belongs to, so overlapping statements for the same account
    /// produce identical fingerprints while different accounts never collide.
    /// </summary>
    public static string AccountKey(string? institution, AccountType accountType, string? accountMask) =>
        $"{(institution ?? "unknown").ToLowerInvariant()}|{accountType}|{accountMask ?? "----"}";

    public static IReadOnlyList<NormalizedTransaction> Normalize(ParsedStatement parsed, string accountKey)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<NormalizedTransaction>(parsed.Transactions.Count);

        foreach (var transaction in parsed.Transactions.OrderBy(t => t.Date).ThenBy(t => t.Page))
        {
            var amount = decimal.Round(transaction.Amount, 2, MidpointRounding.AwayFromZero);
            var descriptionKey = MerchantNormalizer.KeyOf(transaction.Description);
            var identity = string.Create(CultureInfo.InvariantCulture, $"{accountKey}|{transaction.Date:yyyy-MM-dd}|{amount:0.00}|{descriptionKey}");

            var occurrence = occurrences.GetValueOrDefault(identity);
            occurrences[identity] = occurrence + 1;

            result.Add(new NormalizedTransaction(
                transaction.Date,
                transaction.PostingDate,
                transaction.Description,
                amount,
                transaction.Balance,
                parsed.Metadata.Currency,
                transaction.Confidence,
                Fingerprint($"{identity}|{occurrence}"),
                MerchantNormalizer.Normalize(transaction.Description),
                IsReversal: false));
        }

        return MarkReversals(result);
    }

    /// <summary>
    /// A charge followed by an equal credit from the same merchant that is labelled as a reversal,
    /// or that lands within three days with the same descriptor, cancels out and is excluded from
    /// analysis entirely.
    /// </summary>
    internal static IReadOnlyList<NormalizedTransaction> MarkReversals(List<NormalizedTransaction> transactions)
    {
        var reversed = new HashSet<int>();

        for (var i = 0; i < transactions.Count; i++)
        {
            var charge = transactions[i];
            if (charge.Amount >= 0 || reversed.Contains(i))
            {
                continue;
            }

            for (var j = 0; j < transactions.Count; j++)
            {
                var credit = transactions[j];
                if (j == i || reversed.Contains(j) || credit.Amount != -charge.Amount || credit.Merchant.Key != charge.Merchant.Key)
                {
                    continue;
                }

                var days = Math.Abs(credit.Date.DayNumber - charge.Date.DayNumber);
                var labelled = ReversalWords().IsMatch(credit.Description) || ReversalWords().IsMatch(charge.Description);
                var sameDescriptor = MerchantNormalizer.KeyOf(credit.Description) == MerchantNormalizer.KeyOf(charge.Description);

                if ((labelled && days <= 10) || (sameDescriptor && days <= 3))
                {
                    reversed.Add(i);
                    reversed.Add(j);
                    break;
                }
            }
        }

        return transactions.Select((t, index) => reversed.Contains(index) ? t with { IsReversal = true } : t).ToList();
    }

    public static string Fingerprint(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
