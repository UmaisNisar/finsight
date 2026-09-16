using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Infrastructure.Insights;

public sealed record FinancialSnapshot(
    FinancialSummary Summary,
    IReadOnlyList<RecurringSeries> Recurring,
    IReadOnlyList<AnomalyCandidate> Anomalies,
    UserSettings Settings,
    Func<string, CategoryDefinition> ResolveCategory);

/// <summary>Loads a user's transactions and runs the deterministic analytics for a period.</summary>
public sealed class DashboardService(FinSightDbContext db, TimeProvider time)
{
    /// <summary>Recurring detection needs history beyond the selected period.</summary>
    private const int HistoryMonths = 13;

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public async Task<FinancialSnapshot> GetSnapshotAsync(DateRange range, CancellationToken cancellationToken)
    {
        var previous = PeriodResolver.Previous(range);
        var historyStart = new[] { previous.Start, range.End.AddMonths(-HistoryMonths) }.Min();

        var transactions = await db.Transactions.AsNoTracking()
            .Where(t => t.Date >= historyStart && t.Date <= range.End)
            .ToListAsync(cancellationToken);

        var user = await db.Users.AsNoTracking().SingleAsync(cancellationToken);
        var resolve = await CategoryResolverAsync(cancellationToken);

        var analytics = transactions.Select(AnalyticsTransaction.From).ToList();
        var recurring = RecurringDetector.Detect(analytics, range.End);
        var recurringExpenseKeys = recurring.Where(r => !r.IsIncome).Select(r => r.MerchantKey).ToHashSet();

        var summary = FinancialMetricsCalculator.Calculate(analytics, range, recurringExpenseKeys, resolve);
        var anomalies = AnomalyDetector.Detect(analytics, range);

        return new FinancialSnapshot(summary, recurring, anomalies, user.Settings, resolve);
    }

    /// <summary>Recurring series as of today, across all history.</summary>
    public async Task<(IReadOnlyList<RecurringSeries> Series, Func<string, CategoryDefinition> Resolve)> GetRecurringAsync(CancellationToken cancellationToken)
    {
        var from = Today.AddMonths(-HistoryMonths);
        var transactions = await db.Transactions.AsNoTracking().Where(t => t.Date >= from).ToListAsync(cancellationToken);
        var resolve = await CategoryResolverAsync(cancellationToken);
        return (RecurringDetector.Detect(transactions.Select(AnalyticsTransaction.From).ToList(), Today), resolve);
    }

    public async Task<DateOnly?> LatestTransactionDateAsync(CancellationToken cancellationToken) =>
        await db.Transactions.AsNoTracking().OrderByDescending(t => t.Date).Select(t => (DateOnly?)t.Date).FirstOrDefaultAsync(cancellationToken);

    public async Task<Func<string, CategoryDefinition>> CategoryResolverAsync(CancellationToken cancellationToken)
    {
        var custom = await db.CustomCategories.AsNoTracking().ToListAsync(cancellationToken);
        return id => CategoryTaxonomy.Resolve(id, custom);
    }
}
