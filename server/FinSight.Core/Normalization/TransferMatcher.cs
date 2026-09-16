using System.Text.RegularExpressions;
using FinSight.Core.Categories;
using FinSight.Core.Domain;

namespace FinSight.Core.Normalization;

public sealed record TransferCandidate(
    Guid Id,
    string AccountKey,
    AccountType AccountType,
    DateOnly Date,
    decimal Amount,
    string Description,
    string CategoryId,
    TransactionType Type,
    double CategoryConfidence,
    bool IsLocked);

public sealed record TransferPair(Guid OutflowId, Guid InflowId, string CategoryId);

/// <summary>
/// Finds money that left one of the user's accounts and arrived in another (for example a chequing
/// payment to their own credit card). Both sides become transfers so they are neither income nor
/// spending. Matching needs corroboration beyond equal amounts, so a salary deposit and a rent payment
/// of the same size on the same day are never paired.
/// </summary>
public static partial class TransferMatcher
{
    private const int MaxDaysApart = 4;

    [GeneratedRegex(@"\b(TRANSFER|TFR|TRSF|PAYMENT|PMT|E-?TRANSFER|WITHDRAWAL|DEPOSIT|VISA|MASTERCARD|AMEX|CARD|SAVINGS|CHEQUING|CHECKING)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransferHint();

    public static IReadOnlyList<TransferPair> Match(IReadOnlyList<TransferCandidate> candidates)
    {
        var pairs = new List<TransferPair>();
        var used = new HashSet<Guid>();

        var outflows = candidates.Where(c => c.Amount < 0 && !c.IsLocked).OrderBy(c => c.Date);
        var inflowsByAmount = candidates
            .Where(c => c.Amount > 0 && !c.IsLocked)
            .GroupBy(c => c.Amount)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var outflow in outflows)
        {
            if (used.Contains(outflow.Id) || !inflowsByAmount.TryGetValue(-outflow.Amount, out var inflows))
            {
                continue;
            }

            var match = inflows
                .Where(i => !used.Contains(i.Id)
                    && i.AccountKey != outflow.AccountKey
                    && Math.Abs(i.Date.DayNumber - outflow.Date.DayNumber) <= MaxDaysApart
                    && IsCorroborated(outflow, i))
                .OrderBy(i => Math.Abs(i.Date.DayNumber - outflow.Date.DayNumber))
                .FirstOrDefault();

            if (match is null)
            {
                continue;
            }

            used.Add(outflow.Id);
            used.Add(match.Id);

            var category =
                outflow.AccountType == AccountType.CreditCard || match.AccountType == AccountType.CreditCard ? CategoryTaxonomy.CreditCardPayments
                : outflow.CategoryId == CategoryTaxonomy.Investments || match.CategoryId == CategoryTaxonomy.Investments ? CategoryTaxonomy.Investments
                : CategoryTaxonomy.Transfers;

            pairs.Add(new TransferPair(outflow.Id, match.Id, category));
        }

        return pairs;
    }

    private static bool IsCorroborated(TransferCandidate a, TransferCandidate b)
    {
        static bool Signals(TransferCandidate c) =>
            c.Type == TransactionType.Transfer
            || TransferHint().IsMatch(c.Description)
            || c.CategoryConfidence < CategorizationResult.AiThreshold;

        // Paying a credit card from a bank account is the most common own-account transfer.
        var involvesCard = a.AccountType == AccountType.CreditCard || b.AccountType == AccountType.CreditCard;
        return involvesCard ? Signals(a) || Signals(b) : Signals(a) && Signals(b);
    }
}
