using FinSight.Core.Abstractions;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Core.Normalization;
using FinSight.Core.Text;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Pipeline;

public sealed record AiCategorizationOutcome(int MerchantsSent, int MerchantsCategorized, AiFailure? Failure);

/// <summary>
/// Rule-based categorization for every transaction, AI categorization only for what rules could not
/// decide, and transfer matching across the user's accounts.
/// </summary>
public sealed partial class CategorizationService(
    FinSightDbContext db,
    IGeminiService gemini,
    TimeProvider time,
    ILogger<CategorizationService> logger)
{
    private const int MaxAiMerchantsPerRun = 150;

    public async Task ApplyRulesAsync(IReadOnlyList<Transaction> transactions, AccountType accountType, CancellationToken cancellationToken)
    {
        var rules = await LoadRulesAsync(cancellationToken);
        foreach (var transaction in transactions)
        {
            Apply(transaction, RuleCategorizer.Categorize(
                new CategorizationInput(transaction.Description, transaction.MerchantKey, transaction.Amount, accountType), rules));
        }
    }

    /// <summary>
    /// Sends merchants the rules could not categorize confidently to Gemini, once per merchant.
    /// Results are cached as merchant rules so later imports never ask again.
    /// </summary>
    public async Task<AiCategorizationOutcome> CategorizeWithAiAsync(IReadOnlyCollection<Guid> statementIds, CancellationToken cancellationToken)
    {
        if (!gemini.IsConfigured)
        {
            return new AiCategorizationOutcome(0, 0, AiFailure.NotConfigured);
        }

        var pending = await db.Transactions
            .Where(t => statementIds.Contains(t.StatementId) && t.UserCategoryId == null && !t.IsReversal
                && (t.CategorySource == CategorySource.Default || t.CategoryConfidence < CategorizationResult.AiThreshold))
            .ToListAsync(cancellationToken);

        var groups = pending
            .GroupBy(t => (t.MerchantKey, Direction: t.Amount > 0 ? "in" : "out"))
            .Where(g => g.Key.MerchantKey != "unknown")
            .OrderByDescending(g => g.Sum(t => Math.Abs(t.Amount)))
            .Take(MaxAiMerchantsPerRun)
            .ToList();

        if (groups.Count == 0)
        {
            return new AiCategorizationOutcome(0, 0, null);
        }

        var requests = groups.Select((g, i) => new MerchantCategorizationRequest(
            $"M{i + 1}",
            g.Key.MerchantKey,
            g.First().Merchant,
            SensitiveDataMasker.Mask(g.First().Description),
            g.Key.Direction,
            decimal.Round(g.Average(t => Math.Abs(t.Amount)), 2),
            g.Count())).ToList();

        IReadOnlyList<MerchantCategorization> results;
        try
        {
            results = await gemini.CategorizeTransactionsAsync(requests, cancellationToken);
        }
        catch (AiUnavailableException ex)
        {
            LogAiUnavailable(logger, ex.Failure);
            return new AiCategorizationOutcome(requests.Count, 0, ex.Failure);
        }

        var existingRules = await db.MerchantRules.ToDictionaryAsync(r => r.MerchantKey, cancellationToken);
        var now = time.GetUtcNow();
        var userId = pending[0].UserId;

        foreach (var result in results)
        {
            if (existingRules.TryGetValue(result.MerchantKey, out var rule))
            {
                if (rule.Source == CategorySource.User)
                {
                    continue;
                }
            }
            else
            {
                rule = new MerchantRule { UserId = userId, MerchantKey = result.MerchantKey, CategoryId = result.CategoryId };
                db.MerchantRules.Add(rule);
                existingRules[result.MerchantKey] = rule;
            }

            rule.CategoryId = result.CategoryId;
            rule.Type = result.Type;
            rule.DisplayName = result.Merchant;
            rule.Source = CategorySource.Ai;
            rule.Confidence = result.Confidence;
            rule.Reason = result.Reason;
            rule.UpdatedAt = now;

            foreach (var transaction in pending.Where(t => t.MerchantKey == result.MerchantKey))
            {
                var inflow = transaction.Amount > 0;
                var isRefund = inflow && result.Type == TransactionType.Expense;
                Apply(transaction, new CategorizationResult(result.CategoryId, result.Type, CategorySource.Ai, result.Confidence, isRefund, result.Reason));
                if (result.Merchant is not null && transaction.Merchant.Length > 0)
                {
                    transaction.Merchant = result.Merchant;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AiCategorizationOutcome(requests.Count, results.Count, null);
    }

    /// <summary>Pairs money moving between the user's own accounts within the window and marks both sides as transfers.</summary>
    public async Task<int> MatchTransfersAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var transactions = await db.Transactions
            .Include(t => t.Statement)
            .Where(t => t.Date >= from.AddDays(-5) && t.Date <= to.AddDays(5) && !t.IsReversal && t.TransferPairId == null)
            .ToListAsync(cancellationToken);

        var candidates = transactions
            .Where(t => t.Statement is not null)
            .Select(t => new TransferCandidate(
                t.Id,
                TransactionNormalizer.AccountKey(t.Statement!.Institution, t.Statement.AccountType, t.Statement.AccountMask),
                t.Statement.AccountType,
                t.Date,
                t.Amount,
                t.Description,
                t.CategoryId,
                t.Type,
                t.CategoryConfidence,
                IsLocked: t.UserType is not null || t.UserCategoryId is not null || t.IsExcluded))
            .ToList();

        var pairs = TransferMatcher.Match(candidates);
        var byId = transactions.ToDictionary(t => t.Id);

        foreach (var pair in pairs)
        {
            var pairId = Guid.NewGuid();
            foreach (var id in new[] { pair.OutflowId, pair.InflowId })
            {
                var transaction = byId[id];
                transaction.Type = TransactionType.Transfer;
                transaction.CategoryId = pair.CategoryId;
                transaction.CategorySource = CategorySource.Rule;
                transaction.CategoryConfidence = Math.Max(transaction.CategoryConfidence, 0.9);
                transaction.IsRefund = false;
                transaction.TransferPairId = pairId;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return pairs.Count;
    }

    private async Task<Dictionary<string, MerchantRule>> LoadRulesAsync(CancellationToken cancellationToken) =>
        await db.MerchantRules.ToDictionaryAsync(r => r.MerchantKey, cancellationToken);

    private static void Apply(Transaction transaction, CategorizationResult result)
    {
        transaction.CategoryId = result.CategoryId;
        transaction.Type = result.Type;
        transaction.CategorySource = result.Source;
        transaction.CategoryConfidence = result.Confidence;
        transaction.IsRefund = result.IsRefund;
    }

    [LoggerMessage(LogLevel.Warning, "AI categorization skipped: {Failure}")]
    private static partial void LogAiUnavailable(ILogger logger, AiFailure failure);
}
