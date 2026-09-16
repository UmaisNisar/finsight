namespace FinSight.Core.Analytics;

public sealed record CategorySpending(
    string CategoryId,
    string Name,
    string GroupId,
    string GroupName,
    decimal Amount,
    decimal SharePercent,
    int TransactionCount,
    decimal PreviousAmount,
    decimal? ChangePercent,
    decimal MonthlyAverage,
    bool IsFixed);

public sealed record CategoryGroupSpending(string GroupId, string Name, decimal Amount, decimal SharePercent, decimal PreviousAmount, decimal? ChangePercent);

public sealed record IncomeSource(string CategoryId, string Name, decimal Amount, int TransactionCount);

public sealed record MerchantSpending(string MerchantKey, string Merchant, string CategoryId, decimal Amount, int TransactionCount);

public sealed record NotableTransaction(Guid Id, DateOnly Date, string Merchant, string CategoryId, decimal Amount);

public sealed record MonthlyCashFlow(DateOnly Month, decimal Income, decimal Expenses, decimal NetCashFlow, decimal? SavingsRate, bool HasData);

public sealed record PeriodComparison(
    DateRange Range,
    bool HasData,
    decimal Income,
    decimal Expenses,
    decimal? IncomeChangePercent,
    decimal? ExpenseChangePercent);

public sealed record TransferSummary(int Count, decimal Total);

public sealed record DataCoverage(DateOnly? FirstTransaction, DateOnly? LastTransaction, int MonthsInRange, int MonthsWithData)
{
    public bool IsPartial => MonthsWithData < MonthsInRange;
}

/// <summary>
/// Every number the dashboard shows and the AI is given. Computed in code, never by the model.
/// Amounts are positive magnitudes in the user's currency. Refunds reduce <see cref="Expenses"/>;
/// transfers are excluded from both income and expenses.
/// </summary>
public sealed record FinancialSummary(
    DateRange Range,
    decimal Income,
    decimal Expenses,
    decimal GrossSpending,
    decimal Refunds,
    decimal NetCashFlow,
    decimal? SavingsRate,
    decimal AverageMonthlyIncome,
    decimal AverageMonthlyExpenses,
    decimal FixedExpenses,
    decimal VariableExpenses,
    decimal Fees,
    decimal InterestEarned,
    TransferSummary Transfers,
    PeriodComparison Previous,
    IReadOnlyList<CategorySpending> Categories,
    IReadOnlyList<CategoryGroupSpending> Groups,
    IReadOnlyList<IncomeSource> IncomeSources,
    IReadOnlyList<MerchantSpending> TopMerchants,
    IReadOnlyList<NotableTransaction> LargestExpenses,
    IReadOnlyList<MonthlyCashFlow> Monthly,
    int TransactionCount,
    DataCoverage Coverage);
