using FinSight.Core.Categories;
using FinSight.Core.Domain;

namespace FinSight.Core.Insights;

/// <param name="Ref">Opaque reference ("M1") so the model never sees internal ids.</param>
/// <param name="SampleDescription">A masked statement descriptor; card and account numbers are already removed.</param>
public sealed record MerchantCategorizationRequest(string Ref, string MerchantKey, string Merchant, string SampleDescription, string Direction, decimal TypicalAmount, int Occurrences);

public sealed record MerchantCategorization(string MerchantKey, string CategoryId, TransactionType Type, string? Merchant, double Confidence, string Reason);

public sealed record RecurringReviewRequest(string Ref, string Merchant, string Category, decimal Amount, string Frequency, int Occurrences, bool AmountVaries);

public enum RecurringKind
{
    Subscription,
    Bill,
    Membership,
    Loan,
    Habit,
    NotRecurring,
}

public sealed record RecurringReview(string Ref, RecurringKind Kind, string Reason);

#pragma warning disable CA2227, CA1002
public sealed class RawMerchantCategorizationResponse
{
    public List<RawMerchantCategorization>? Results { get; set; }
}

public sealed class RawMerchantCategorization
{
    public string? Ref { get; set; }
    public string? CategoryId { get; set; }
    public string? Merchant { get; set; }
    public double? Confidence { get; set; }
    public string? Reason { get; set; }
}

public sealed class RawRecurringReviewResponse
{
    public List<RawRecurringReview>? Results { get; set; }
}

public sealed class RawRecurringReview
{
    public string? Ref { get; set; }
    public string? Kind { get; set; }
    public string? Reason { get; set; }
}
#pragma warning restore CA2227, CA1002

public static class MerchantCategorizationValidator
{
    /// <summary>AI answers below this confidence are discarded; the transaction stays uncategorized.</summary>
    public const double MinimumConfidence = 0.55;

    /// <summary>Categories the model may choose for a given direction of money.</summary>
    public static IReadOnlyList<CategoryDefinition> AllowedCategories(string direction) =>
        CategoryTaxonomy.All
            .Where(c => direction == "in"
                ? c.Kind is CategoryKind.Income or CategoryKind.Transfer
                : c.Kind is CategoryKind.Expense or CategoryKind.Transfer)
            .ToList();

    public static IReadOnlyList<MerchantCategorization> Validate(RawMerchantCategorizationResponse raw, IReadOnlyList<MerchantCategorizationRequest> requests)
    {
        var byRef = requests.ToDictionary(r => r.Ref, StringComparer.OrdinalIgnoreCase);
        var results = new List<MerchantCategorization>();

        foreach (var item in raw.Results ?? [])
        {
            if (item.Ref is null || !byRef.TryGetValue(item.Ref.Trim(), out var request) || results.Any(r => r.MerchantKey == request.MerchantKey))
            {
                continue;
            }

            var category = item.CategoryId is null ? null : CategoryTaxonomy.Find(item.CategoryId.Trim());
            if (category is null || !AllowedCategories(request.Direction).Contains(category))
            {
                continue;
            }

            var confidence = Math.Clamp(item.Confidence ?? 0, 0, 1);
            if (confidence < MinimumConfidence || category.Id == CategoryTaxonomy.Uncategorized)
            {
                continue;
            }

            var merchant = item.Merchant?.Trim();
            if (merchant is null || merchant.Length is < 2 or > 40 || !merchant.Any(char.IsLetter) || merchant.Contains('•'))
            {
                merchant = null;
            }

            var reason = item.Reason?.Trim() ?? "Categorized by AI";
            results.Add(new MerchantCategorization(
                request.MerchantKey,
                category.Id,
                category.Kind switch
                {
                    CategoryKind.Transfer => TransactionType.Transfer,
                    CategoryKind.Income => TransactionType.Income,
                    _ => TransactionType.Expense,
                },
                merchant,
                Math.Round(confidence, 2),
                reason.Length > 200 ? reason[..200] : reason));
        }

        return results;
    }

    public static IReadOnlyList<RecurringReview> ValidateRecurring(RawRecurringReviewResponse raw, IReadOnlyList<RecurringReviewRequest> requests)
    {
        var refs = requests.Select(r => r.Ref).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (raw.Results ?? [])
            .Where(r => r.Ref is not null && refs.Contains(r.Ref.Trim()))
            .Select(r => (Ref: r.Ref!.Trim(), Kind: Enum.TryParse<RecurringKind>(r.Kind?.Replace("_", "", StringComparison.Ordinal), true, out var kind) ? kind : (RecurringKind?)null, r.Reason))
            .Where(r => r.Kind is not null)
            .DistinctBy(r => r.Ref)
            .Select(r => new RecurringReview(r.Ref, r.Kind!.Value, (r.Reason ?? string.Empty).Trim() is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, 200)] : r.Kind.Value.ToString()))
            .ToList();
    }
}
