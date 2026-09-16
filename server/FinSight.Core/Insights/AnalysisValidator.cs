using System.Globalization;
using System.Text.RegularExpressions;

namespace FinSight.Core.Insights;

/// <summary>
/// Checks a model's analysis against the facts it was given. Structural problems are corrected or
/// removed; every number the app owns (spending, amounts, dates) is overwritten with the computed
/// value; currency figures in prose that match no known number are reported as unverified.
/// </summary>
public static partial class AnalysisValidator
{
    private const int MaxInsights = 6;
    private const int MaxOpportunities = 5;
    private const int MaxRecurring = 12;
    private const int MaxAnomalies = 8;
    private const int MaxRecommendations = 6;
    private const int MaxSummaryLength = 900;
    private const int MaxTextLength = 600;

    [GeneratedRegex(@"[$€£]\s?(\d{1,3}(?:,\d{3})+|\d+)(\.\d{1,2})?")]
    private static partial Regex CurrencyFigure();

    public static ValidatedAnalysis Validate(RawAnalysis raw, FinancialFacts facts)
    {
        var corrections = new List<AnalysisCorrection>();

        var summary = Clean(raw.Summary, MaxSummaryLength);
        if (summary.Length == 0)
        {
            corrections.Add(new("summary", "The AI returned no summary."));
            summary = "An AI summary is not available for this period.";
        }

        var insights = (raw.KeyInsights ?? [])
            .Select(i => (Title: Clean(i.Title, 90), Description: Clean(i.Description, MaxTextLength), i.Severity))
            .Where(i => i.Title.Length > 0 && i.Description.Length > 0)
            .Take(MaxInsights)
            .Select(i => new KeyInsight(i.Title, i.Description, i.Severity?.ToLowerInvariant() switch
            {
                "attention" => InsightSeverity.Attention,
                "positive" => InsightSeverity.Positive,
                _ => InsightSeverity.Info,
            }))
            .ToList();

        var opportunities = ValidateOpportunities(raw.SavingsOpportunities ?? [], facts, corrections);
        var recurring = ValidateRecurring(raw.RecurringExpenses ?? [], facts, corrections);
        var anomalies = ValidateAnomalies(raw.Anomalies ?? [], facts, corrections);

        var recommendations = (raw.Recommendations ?? [])
            .Select(r => new Recommendation(Clean(r.Title, 90), Clean(r.Description, MaxTextLength), NullIfEmpty(Clean(r.PotentialImpact, 160))))
            .Where(r => r.Title.Length > 0 && r.Description.Length > 0)
            .Take(MaxRecommendations)
            .ToList();

        var caveats = (raw.Caveats ?? []).Select(c => Clean(c, 300)).Where(c => c.Length > 0).Take(4).ToList();

        var analysis = new FinancialAnalysis(summary, insights, opportunities, recurring, anomalies, recommendations, caveats);
        CheckFigures(analysis, facts, corrections);

        return new ValidatedAnalysis(analysis, corrections);
    }

    private static List<SavingsOpportunity> ValidateOpportunities(List<RawSavingsOpportunity> raw, FinancialFacts facts, List<AnalysisCorrection> corrections)
    {
        var result = new List<SavingsOpportunity>();

        foreach (var item in raw)
        {
            var category = facts.Categories.FirstOrDefault(c =>
                string.Equals(c.Id, item.CategoryId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, item.CategoryId, StringComparison.OrdinalIgnoreCase));

            if (category is null)
            {
                corrections.Add(new("savingsOpportunities", $"Removed a suggestion for \"{Clean(item.CategoryId, 40)}\", which is not a category in your data."));
                continue;
            }

            if (result.Any(o => o.CategoryId == category.Id))
            {
                continue;
            }

            var current = category.MonthlyAverage;
            var explanation = Clean(item.Explanation, MaxTextLength);
            if (current <= 0 || explanation.Length == 0)
            {
                continue;
            }

            decimal? target = item.SuggestedMonthlyTarget;
            decimal? savings = item.EstimatedMonthlySavings;

            if (target is not null && (target < 0 || target >= current))
            {
                corrections.Add(new("savingsOpportunities", $"Ignored a {category.Name} target that was not below current spending."));
                target = null;
            }

            if (target is not null)
            {
                var computed = decimal.Round(current - target.Value, 2);
                if (savings is not null && Math.Abs(savings.Value - computed) > 1)
                {
                    corrections.Add(new("savingsOpportunities", $"Recalculated {category.Name} savings from {Format(savings.Value)} to {Format(computed)}."));
                }

                savings = computed;
                target = decimal.Round(target.Value, 2);
            }
            else if (savings is not null && (savings <= 0 || savings > current))
            {
                corrections.Add(new("savingsOpportunities", $"Removed an impossible {category.Name} savings estimate."));
                savings = null;
            }

            result.Add(new SavingsOpportunity(category.Id, category.Name, current, target, savings is null ? null : decimal.Round(savings.Value, 2), explanation));
            if (result.Count == MaxOpportunities)
            {
                break;
            }
        }

        return result;
    }

    private static List<RecurringExpenseInsight> ValidateRecurring(List<RawRecurring> raw, FinancialFacts facts, List<AnalysisCorrection> corrections)
    {
        var result = new List<RecurringExpenseInsight>();
        foreach (var item in raw)
        {
            var match = facts.RecurringExpenses.FirstOrDefault(r => string.Equals(r.Merchant, item.Merchant?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                corrections.Add(new("recurringExpenses", $"Removed \"{Clean(item.Merchant, 40)}\", which was not detected as recurring in your transactions."));
                continue;
            }

            if (result.Any(r => r.Merchant == match.Merchant))
            {
                continue;
            }

            result.Add(new RecurringExpenseInsight(match.Merchant, match.Amount, match.Frequency, NullIfEmpty(Clean(item.Note, 240))));
            if (result.Count == MaxRecurring)
            {
                break;
            }
        }

        return result;
    }

    private static List<AnomalyInsight> ValidateAnomalies(List<RawAnomaly> raw, FinancialFacts facts, List<AnalysisCorrection> corrections)
    {
        var result = new List<AnomalyInsight>();
        foreach (var item in raw)
        {
            var match = facts.AnomalyCandidates.FirstOrDefault(a => string.Equals(a.Ref, item.Ref?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                corrections.Add(new("anomalies", "Removed an unusual-spending item that did not match any flagged transaction."));
                continue;
            }

            var explanation = Clean(item.Explanation, MaxTextLength);
            if (explanation.Length == 0 || result.Any(r => r.Ref == match.Ref))
            {
                continue;
            }

            var description = Clean(item.Description, 160);
            result.Add(new AnomalyInsight(match.Ref, match.Merchant, description.Length > 0 ? description : match.Merchant, match.Amount, match.Date, explanation));
            if (result.Count == MaxAnomalies)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Flags currency figures in prose that cannot be traced to a computed value.</summary>
    private static void CheckFigures(FinancialAnalysis analysis, FinancialFacts facts, List<AnalysisCorrection> corrections)
    {
        var known = KnownValues(facts, analysis).ToList();

        var texts = new List<(string Section, string Text)> { ("summary", analysis.Summary) };
        texts.AddRange(analysis.KeyInsights.Select(i => ("keyInsights", i.Description)));
        texts.AddRange(analysis.SavingsOpportunities.Select(o => ("savingsOpportunities", o.Explanation)));
        texts.AddRange(analysis.Anomalies.Select(a => ("anomalies", a.Explanation)));
        texts.AddRange(analysis.Recommendations.Select(r => ("recommendations", $"{r.Description} {r.PotentialImpact}")));

        var unverified = new HashSet<string>();
        foreach (var (section, text) in texts)
        {
            foreach (Match match in CurrencyFigure().Matches(text))
            {
                var number = decimal.Parse(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal) + match.Groups[2].Value, CultureInfo.InvariantCulture);
                var matches = known.Any(v => Math.Abs(Math.Abs(v) - number) <= Math.Max(1.5m, Math.Abs(v) * 0.015m));
                if (!matches && unverified.Add(match.Value))
                {
                    corrections.Add(new(section, $"The figure {match.Value} could not be matched to your data. Treat it as an estimate."));
                }
            }
        }
    }

    private static IEnumerable<decimal> KnownValues(FinancialFacts facts, FinancialAnalysis analysis)
    {
        decimal[] totals =
        [
            facts.Income, facts.Expenses, facts.NetCashFlow, facts.AverageMonthlyIncome, facts.AverageMonthlyExpenses,
            facts.FixedExpenses, facts.VariableExpenses, facts.Refunds, facts.Fees, facts.TransfersExcludedFromSpending,
        ];

        foreach (var value in totals)
        {
            yield return value;
        }

        if (facts.PreviousPeriod is { } previous)
        {
            yield return previous.Income;
            yield return previous.Expenses;
            yield return facts.Income - previous.Income;
            yield return facts.Expenses - previous.Expenses;
        }

        foreach (var c in facts.Categories)
        {
            yield return c.Amount;
            yield return c.MonthlyAverage;
            yield return c.PreviousAmount;
            yield return c.Amount - c.PreviousAmount;
            yield return c.MonthlyAverage * 12;
        }

        foreach (var value in facts.IncomeSources.Select(s => s.Amount)
            .Concat(facts.TopMerchants.Select(m => m.Amount))
            .Concat(facts.RecurringExpenses.SelectMany(r => new[] { r.Amount, r.MonthlyEquivalent, r.MonthlyEquivalent * 12 }))
            .Concat(facts.AnomalyCandidates.SelectMany(a => new[] { a.Amount, a.TypicalAmount ?? 0 }))
            .Concat(facts.LargestTransactions.Select(t => t.Amount))
            .Concat(facts.MonthlyTrend.SelectMany(m => new[] { m.Income, m.Expenses, m.NetCashFlow })))
        {
            yield return value;
        }

        foreach (var o in analysis.SavingsOpportunities)
        {
            yield return o.CurrentMonthlySpending;
            if (o.SuggestedMonthlyTarget is { } target)
            {
                yield return target;
            }

            if (o.EstimatedMonthlySavings is { } savings)
            {
                yield return savings;
                yield return savings * 12;
            }
        }

        if (facts.RecurringExpenses.Count > 0)
        {
            yield return facts.RecurringExpenses.Sum(r => r.MonthlyEquivalent);
        }
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = Regex.Replace(value.Trim(), @"\s+", " ");
        return trimmed.Length <= maxLength ? trimmed : trimmed[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
