namespace FinSight.Core.Analytics;

public enum AnomalyKind
{
    /// <summary>Much larger than this category's usual transaction.</summary>
    UnusuallyLarge,

    /// <summary>A large payment to a merchant not seen before.</summary>
    NewMerchant,

    /// <summary>Same merchant and amount twice on the same day.</summary>
    PossibleDuplicate,
}

public sealed record AnomalyCandidate(Guid TransactionId, DateOnly Date, string Merchant, string CategoryId, decimal Amount, AnomalyKind Kind, decimal? TypicalAmount);

/// <summary>
/// Flags transactions worth a second look. The AI may explain these candidates, but it can only
/// report anomalies that appear in this list.
/// </summary>
public static class AnomalyDetector
{
    private const decimal LargeMultiple = 3m;
    private const decimal MinimumAmount = 50m;

    /// <param name="history">Transactions before and within the range; history gives each category its baseline.</param>
    public static IReadOnlyList<AnomalyCandidate> Detect(IReadOnlyList<AnalyticsTransaction> history, DateRange range, int limit = 8)
    {
        var spending = history.Where(t => t.IsSpending && t.Amount < 0).ToList();
        var inRange = spending.Where(t => range.Contains(t.Date)).ToList();
        if (inRange.Count == 0)
        {
            return [];
        }

        var overallMedian = RecurringDetector.Median(spending.Select(t => -t.Amount).ToList());
        var candidates = new List<AnomalyCandidate>();

        foreach (var transaction in inRange)
        {
            var amount = -transaction.Amount;
            if (amount < MinimumAmount)
            {
                continue;
            }

            var peers = spending.Where(t => t.CategoryId == transaction.CategoryId && t.Id != transaction.Id).Select(t => -t.Amount).ToList();
            if (peers.Count >= 4)
            {
                var typical = RecurringDetector.Median(peers);
                if (typical > 0 && amount >= typical * LargeMultiple)
                {
                    candidates.Add(new AnomalyCandidate(transaction.Id, transaction.Date, transaction.Merchant, transaction.CategoryId, amount, AnomalyKind.UnusuallyLarge, decimal.Round(typical, 2)));
                    continue;
                }
            }

            var seenBefore = spending.Any(t => t.MerchantKey == transaction.MerchantKey && t.Date < transaction.Date);
            if (!seenBefore && overallMedian > 0 && amount >= overallMedian * 5 && amount >= MinimumAmount * 4)
            {
                candidates.Add(new AnomalyCandidate(transaction.Id, transaction.Date, transaction.Merchant, transaction.CategoryId, amount, AnomalyKind.NewMerchant, decimal.Round(overallMedian, 2)));
            }
        }

        var duplicates = inRange
            .GroupBy(t => (t.MerchantKey, t.Date, t.Amount))
            .Where(g => g.Count() > 1 && -g.Key.Amount >= 10)
            .Select(g => g.Skip(1).First())
            .Select(t => new AnomalyCandidate(t.Id, t.Date, t.Merchant, t.CategoryId, -t.Amount, AnomalyKind.PossibleDuplicate, null));

        return candidates
            .Concat(duplicates)
            .DistinctBy(c => c.TransactionId)
            .OrderByDescending(c => c.Amount)
            .Take(limit)
            .ToList();
    }
}
