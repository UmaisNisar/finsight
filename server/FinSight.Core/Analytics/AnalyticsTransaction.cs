using FinSight.Core.Domain;

namespace FinSight.Core.Analytics;

/// <summary>The effective view of a transaction used for all calculations: user overrides applied.</summary>
public sealed record AnalyticsTransaction(
    Guid Id,
    DateOnly Date,
    decimal Amount,
    TransactionType Type,
    string CategoryId,
    string Merchant,
    string MerchantKey,
    bool IsRefund,
    bool IsIgnored)
{
    public static AnalyticsTransaction From(Transaction t) => new(
        t.Id,
        t.Date,
        t.Amount,
        t.EffectiveType,
        t.EffectiveCategoryId,
        t.EffectiveMerchant,
        t.UserMerchant is null ? t.MerchantKey : Normalization.MerchantNormalizer.KeyOf(t.UserMerchant),
        t.IsRefund && t.UserType is null or TransactionType.Expense,
        t.IsExcluded || t.IsReversal);

    public bool IsIncome => !IsIgnored && Type == TransactionType.Income && Amount > 0;

    /// <summary>Expense-type rows: purchases (negative) and refunds (positive), which offset each other.</summary>
    public bool IsSpending => !IsIgnored && Type == TransactionType.Expense;

    public bool IsTransfer => !IsIgnored && Type == TransactionType.Transfer;
}
