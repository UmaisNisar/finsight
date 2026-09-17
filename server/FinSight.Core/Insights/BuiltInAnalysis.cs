using System.Globalization;

namespace FinSight.Core.Insights;

/// <summary>
/// Writes an analysis without AI, from exactly the facts Gemini would have been sent. Used when Gemini is unavailable (no key,
/// key refused, quota used up, daily limit reached), so the insight card still says something useful. Every sentence is
/// derived from a computed figure and the result goes through <see cref="AnalysisValidator"/> like a model's answer would, so
/// figures stay consistent. Deterministic: the same facts always produce the same text.
/// </summary>
public static class BuiltInAnalysis
{
    /// <summary>Monthly spending below this is too small for a savings suggestion to be worth reading.</summary>
    private const decimal MinimumOpportunity = 100m;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] FlexiblePrefixes = ["food.", "shopping.", "entertainment.", "transportation.ride-sharing", "personal.care"];

    public static ValidatedAnalysis Write(FinancialFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var money = MoneyFormat(facts.Currency);
        var raw = new RawAnalysis
        {
            Summary = Summary(facts, money),
            KeyInsights = KeyInsights(facts, money),
            SavingsOpportunities = Opportunities(facts, money),
            RecurringExpenses = facts.RecurringExpenses.Select(r => new RawRecurring { Merchant = r.Merchant }).ToList(),
            Anomalies = facts.AnomalyCandidates.Select(a => new RawAnomaly
            {
                Ref = a.Ref,
                Description = a.Merchant,
                Explanation = Sentence(a.Reason) + (a.TypicalAmount is { } typical and > 0 ? $", where a typical payment is about {money(typical)}." : "."),
            }).ToList(),
            Recommendations = Recommendations(facts, money),
            Caveats = Caveats(facts),
        };

        return AnalysisValidator.Validate(raw, facts);
    }

    private static string Summary(FinancialFacts facts, Func<decimal, string> money)
    {
        var parts = new List<string>();

        if (facts.Income > 0)
        {
            parts.Add(facts.NetCashFlow >= 0
                ? $"In {facts.Period}, you earned {money(facts.Income)} and spent {money(facts.Expenses)}, leaving {money(facts.NetCashFlow)}."
                : $"In {facts.Period}, you earned {money(facts.Income)} and spent {money(facts.Expenses)}, which was {money(facts.NetCashFlow)} more than you earned.");
        }
        else
        {
            parts.Add($"In {facts.Period}, you spent {money(facts.Expenses)} and no income was recorded.");
        }

        if (facts.PreviousPeriod is { ExpenseChangePercent: { } change } previous)
        {
            parts.Add(Math.Abs(change) < 2
                ? $"Spending was about the same as in {previous.Period}."
                : $"Spending was {(change > 0 ? "up" : "down")} {Percent(Math.Abs(change))} on {previous.Period}.");
        }

        if (facts.Categories is [{ Amount: > 0 } top, ..])
        {
            parts.Add($"Your largest category was {top.Name} at {money(top.Amount)}, {Percent(top.SharePercent)} of spending.");
        }

        return string.Join(' ', parts);
    }

    private static List<RawInsight> KeyInsights(FinancialFacts facts, Func<decimal, string> money)
    {
        var insights = new List<RawInsight>();

        if (facts.Income > 0 && facts.SavingsRatePercent is { } rate)
        {
            var previous = facts.PreviousPeriod;
            decimal? previousRate = previous is { Income: > 0 }
                ? decimal.Round((previous.Income - previous.Expenses) / previous.Income * 100, 1, MidpointRounding.AwayFromZero)
                : null;

            if (rate < 0)
            {
                insights.Add(Insight("Spending was more than income", $"You spent {money(facts.NetCashFlow)} more than you earned in {facts.Period}.", "attention"));
            }
            else
            {
                var comparison = previousRate switch
                {
                    null => string.Empty,
                    { } p when Math.Abs(rate - p) < 1 => $" That's about the same as {previous!.Period}.",
                    { } p => $" That's {(rate > p ? "up" : "down")} from {Percent(p)} in {previous!.Period}.",
                };
                var severity = previousRate switch
                {
                    { } p when rate - p >= 1 => "positive",
                    { } p when p - rate >= 5 => "attention",
                    null when rate >= 20 => "positive",
                    _ => "info",
                };
                insights.Add(Insight($"You kept {Percent(rate)} of your income",
                    $"Income was {money(facts.Income)} and spending {money(facts.Expenses)}, leaving {money(facts.NetCashFlow)}.{comparison}", severity));
            }
        }
        else if (facts.Income <= 0 && facts.Expenses > 0)
        {
            insights.Add(Insight("No income recorded", $"No income was recorded in {facts.Period}, so there's no savings rate. Spending was {money(facts.Expenses)}.", "info"));
        }

        if (facts.PreviousPeriod is { } before)
        {
            var changes = facts.Categories
                .Select(c => (Category: c, Delta: c.Amount - c.PreviousAmount))
                .Where(x => Math.Abs(x.Delta) >= 50 && (x.Category.PreviousAmount <= 0 || Math.Abs(x.Delta) / x.Category.PreviousAmount >= 0.15m))
                .OrderByDescending(x => Math.Abs(x.Delta))
                .ThenBy(x => x.Category.Name, StringComparer.Ordinal)
                .Take(2);

            foreach (var (category, delta) in changes)
            {
                insights.Add(category.PreviousAmount <= 0
                    ? Insight($"New spending on {category.Name}", $"You spent {money(category.Amount)} on {category.Name}, with nothing in {before.Period}.", "info")
                    : delta > 0
                        ? Insight($"More on {category.Name}", $"You spent {money(category.Amount)} on {category.Name}, {money(delta)} more than in {before.Period} ({money(category.PreviousAmount)}).", "attention")
                        : Insight($"Less on {category.Name}", $"You spent {money(category.Amount)} on {category.Name}, {money(delta)} less than in {before.Period} ({money(category.PreviousAmount)}).", "positive"));
            }
        }

        if (facts.AnomalyCandidates is [var anomaly, ..])
        {
            var count = facts.AnomalyCandidates.Count;
            insights.Add(Insight(count == 1 ? "One transaction stood out" : $"{count} transactions stood out",
                $"{anomaly.Merchant}, {money(anomaly.Amount)} on {ShortDate(anomaly.Date)}: {anomaly.Reason}.", "attention"));
        }

        if (facts.TopMerchants is [{ Amount: > 0 } merchant, ..])
        {
            insights.Add(Insight($"Most spent at {merchant.Merchant}",
                $"{money(merchant.Amount)} across {Plural(merchant.Transactions, "transaction")}, in {merchant.Category}.", "info"));
        }

        if (facts.Fees >= 1)
        {
            insights.Add(Insight("Fees and interest", $"You paid {money(facts.Fees)} in bank fees and interest charges.", facts.Fees >= 25 ? "attention" : "info"));
        }

        if (facts.RecurringExpenses.Count > 0)
        {
            insights.Add(Insight("Recurring payments",
                $"{Plural(facts.RecurringExpenses.Count, "recurring payment")} add up to about {money(facts.RecurringExpenses.Sum(r => r.MonthlyEquivalent))} a month.", "info"));
        }

        return insights;
    }

    /// <summary>
    /// Conservative on purpose: only flexible categories worth at least <see cref="MinimumOpportunity"/> a month, and only a
    /// tenth off. Categories that rose on the previous period come first.
    /// </summary>
    private static List<RawSavingsOpportunity> Opportunities(FinancialFacts facts, Func<decimal, string> money)
    {
        var flexible = facts.Categories
            .Where(c => !c.IsFixed && c.MonthlyAverage >= MinimumOpportunity && FlexiblePrefixes.Any(p => c.Id.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        var rising = flexible
            .Where(c => c.ChangePercent is >= 15)
            .OrderByDescending(c => c.Amount - c.PreviousAmount)
            .ThenBy(c => c.Name, StringComparer.Ordinal);
        var large = flexible
            .Where(c => c.ChangePercent is not >= 15 && c.SharePercent >= 10)
            .OrderByDescending(c => c.MonthlyAverage)
            .ThenBy(c => c.Name, StringComparer.Ordinal);

        return rising.Concat(large).Take(3).Select(c =>
        {
            var savings = decimal.Round(c.MonthlyAverage * 0.1m, 0, MidpointRounding.AwayFromZero);
            var explanation = c.ChangePercent is >= 15 && facts.PreviousPeriod is { } previous
                ? $"{c.Name} rose {Percent(c.ChangePercent.Value)} compared with {previous.Period}. Spending a tenth less would keep about {money(savings)} a month."
                : $"{c.Name} is one of your larger flexible costs. Spending a tenth less would keep about {money(savings)} a month.";
            return new RawSavingsOpportunity
            {
                CategoryId = c.Id,
                SuggestedMonthlyTarget = c.MonthlyAverage - savings,
                EstimatedMonthlySavings = savings,
                Explanation = explanation,
            };
        }).ToList();
    }

    private static List<RawRecommendation> Recommendations(FinancialFacts facts, Func<decimal, string> money)
    {
        var recommendations = new List<RawRecommendation>();
        if (facts.RecurringExpenses.Count > 0)
        {
            recommendations.Add(new RawRecommendation
            {
                Title = "Review your recurring payments",
                Description = $"They add up to about {money(facts.RecurringExpenses.Sum(r => r.MonthlyEquivalent))} a month. Cancel anything you no longer use.",
            });
        }

        if (facts.AnomalyCandidates.Count > 0)
        {
            recommendations.Add(new RawRecommendation
            {
                Title = "Check the transactions that stood out",
                Description = "Make sure you recognise each one. Unusual doesn't mean wrong, but a quick look catches mistakes early.",
            });
        }

        return recommendations;
    }

    private static List<string> Caveats(FinancialFacts facts)
    {
        var caveats = new List<string>();
        if (facts.MonthsWithData < facts.MonthsInPeriod)
        {
            caveats.Add($"Only {facts.MonthsWithData} of {facts.MonthsInPeriod} months in this period have imported statements, so totals may be incomplete.");
        }

        if (facts.PreviousPeriod is null)
        {
            caveats.Add("There's no data for the previous period, so nothing here is compared with it.");
        }

        if (facts.TransfersExcludedFromSpending > 0)
        {
            caveats.Add("Transfers between your own accounts, including card payments, aren't counted as income or spending.");
        }

        return caveats;
    }

    private static RawInsight Insight(string title, string description, string severity) =>
        new() { Title = title, Description = description, Severity = severity };

    /// <summary>Whole units with the symbol <see cref="AnalysisValidator"/> recognises, so every figure is checked.</summary>
    private static Func<decimal, string> MoneyFormat(string currency)
    {
        var prefix = currency.ToUpperInvariant() switch
        {
            "CAD" or "USD" => "$",
            "AUD" => "A$",
            "EUR" => "€",
            "GBP" => "£",
            "PKR" => "Rs ",
            var code => code + " ",
        };
        return amount => prefix + decimal.Round(Math.Abs(amount), 0, MidpointRounding.AwayFromZero).ToString("#,0", Invariant);
    }

    private static string Percent(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", Invariant) + "%";

    private static string Plural(int count, string noun) => $"{count.ToString(Invariant)} {noun}{(count == 1 ? "" : "s")}";

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string ShortDate(string isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date) ? date.ToString("MMM d", Invariant) : isoDate;
}
