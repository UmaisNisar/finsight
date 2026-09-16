using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Normalization;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

public sealed class TransactionQuery
{
    public string? Search { get; set; }
    public string? CategoryId { get; set; }
    public string? GroupId { get; set; }

    /// <summary>income | expense | transfer</summary>
    public string? Type { get; set; }

    public string? Merchant { get; set; }
    public Guid? StatementId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool IncludeExcluded { get; set; } = true;

    /// <summary>date-desc | date-asc | amount-desc | amount-asc</summary>
    public string? Sort { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

[ApiController]
[Route("api/transactions")]
public sealed class TransactionsController(FinSightDbContext db, DashboardService dashboard, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<TransactionPage> List([FromQuery] TransactionQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 10, 200);

        var dbQuery = db.Transactions.AsNoTracking().Include(t => t.Statement).AsQueryable();
        if (query.From is not null)
        {
            dbQuery = dbQuery.Where(t => t.Date >= query.From);
        }

        if (query.To is not null)
        {
            dbQuery = dbQuery.Where(t => t.Date <= query.To);
        }

        if (query.StatementId is not null)
        {
            dbQuery = dbQuery.Where(t => t.StatementId == query.StatementId);
        }

        if (!query.IncludeExcluded)
        {
            dbQuery = dbQuery.Where(t => !t.IsExcluded);
        }

        // Descriptions are encrypted at rest, so text search and override-aware filters run in memory
        // over this user's rows for the selected dates.
        var resolve = await dashboard.CategoryResolverAsync(cancellationToken);
        IEnumerable<Transaction> filtered = await dbQuery.ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            filtered = filtered.Where(t =>
                t.EffectiveMerchant.Contains(term, StringComparison.OrdinalIgnoreCase)
                || t.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                || resolve(t.EffectiveCategoryId).Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.CategoryId))
        {
            filtered = filtered.Where(t => t.EffectiveCategoryId == query.CategoryId);
        }

        if (!string.IsNullOrWhiteSpace(query.GroupId))
        {
            filtered = filtered.Where(t => resolve(t.EffectiveCategoryId).GroupId == query.GroupId);
        }

        if (!string.IsNullOrWhiteSpace(query.Merchant))
        {
            filtered = filtered.Where(t => t.EffectiveMerchant.Equals(query.Merchant, StringComparison.OrdinalIgnoreCase));
        }

        filtered = query.Type?.ToLowerInvariant() switch
        {
            "income" => filtered.Where(t => t.EffectiveType == TransactionType.Income),
            "expense" => filtered.Where(t => t.EffectiveType == TransactionType.Expense),
            "transfer" => filtered.Where(t => t.EffectiveType == TransactionType.Transfer),
            _ => filtered,
        };

        var list = filtered.ToList();
        var sorted = query.Sort switch
        {
            "date-asc" => list.OrderBy(t => t.Date).ThenBy(t => t.CreatedAt),
            "amount-desc" => list.OrderByDescending(t => Math.Abs(t.Amount)),
            "amount-asc" => list.OrderBy(t => Math.Abs(t.Amount)),
            _ => list.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt),
        };

        var counted = list.Where(t => !t.IsExcluded && !t.IsReversal && t.EffectiveType != TransactionType.Transfer).ToList();
        return new TransactionPage(
            sorted.Skip((page - 1) * pageSize).Take(pageSize).Select(t => t.ToDto(resolve)).ToList(),
            list.Count,
            page,
            pageSize,
            counted.Where(t => t.Amount > 0).Sum(t => t.Amount),
            -counted.Where(t => t.Amount < 0).Sum(t => t.Amount));
    }

    /// <summary>
    /// Edits a transaction: category, merchant name, type (e.g. mark as transfer) or exclusion from analysis.
    /// With <c>applyToMerchant</c>, the category also becomes a rule for every transaction from that merchant.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<TransactionDto>> Update(Guid id, UpdateTransactionRequest request, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.Include(t => t.Statement).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (transaction is null)
        {
            return ApiErrors.NotFound("transaction");
        }

        var resolve = await dashboard.CategoryResolverAsync(cancellationToken);

        if (request.ResetOverrides)
        {
            transaction.UserCategoryId = null;
            transaction.UserMerchant = null;
            transaction.UserType = null;
            transaction.IsExcluded = false;
        }

        if (request.CategoryId is not null)
        {
            var category = resolve(request.CategoryId);
            if (category.Id != request.CategoryId)
            {
                return ApiErrors.BadRequest("invalid_category", "That category doesn't exist.");
            }

            transaction.UserCategoryId = category.Id;

            // Choosing a transfer category implies a transfer; choosing spending on a transfer undoes it.
            var implied = CategoryTaxonomy.ImpliedType(category, transaction.Amount);
            if (request.Type is null && implied != transaction.EffectiveType)
            {
                transaction.UserType = implied;
            }

            if (request.ApplyToMerchant)
            {
                await UpsertMerchantRuleAsync(transaction, category.Id, cancellationToken);
            }
        }

        if (request.Merchant is not null)
        {
            var merchant = request.Merchant.Trim();
            if (merchant.Length is 0 or > 80)
            {
                return ApiErrors.BadRequest("invalid_merchant", "Merchant names need 1 to 80 characters.");
            }

            transaction.UserMerchant = merchant == transaction.Merchant ? null : merchant;
        }

        if (request.Type is not null)
        {
            transaction.UserType = request.Type == transaction.Type ? null : request.Type;
            if (request.Type == TransactionType.Transfer && request.CategoryId is null)
            {
                transaction.UserCategoryId = CategoryTaxonomy.Transfers;
            }
        }

        if (request.IsExcluded is not null)
        {
            transaction.IsExcluded = request.IsExcluded.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        return transaction.ToDto(resolve);
    }

    private async Task UpsertMerchantRuleAsync(Transaction source, string categoryId, CancellationToken cancellationToken)
    {
        var key = source.UserMerchant is null ? source.MerchantKey : MerchantNormalizer.KeyOf(source.UserMerchant);
        var rule = await db.MerchantRules.SingleOrDefaultAsync(r => r.MerchantKey == key, cancellationToken);
        if (rule is null)
        {
            rule = new MerchantRule { UserId = source.UserId, MerchantKey = key, CategoryId = categoryId };
            db.MerchantRules.Add(rule);
        }

        rule.CategoryId = categoryId;
        rule.DisplayName = source.EffectiveMerchant;
        rule.Source = CategorySource.User;
        rule.Type = CategoryTaxonomy.Resolve(categoryId).Kind == CategoryKind.Transfer ? TransactionType.Transfer : null;
        rule.Confidence = 1;
        rule.Reason = "Your rule for this merchant";
        rule.UpdatedAt = time.GetUtcNow();

        // Apply to existing transactions from the same merchant that the user has not edited individually.
        var siblings = await db.Transactions
            .Where(t => t.MerchantKey == source.MerchantKey && t.Id != source.Id && t.UserCategoryId == null)
            .ToListAsync(cancellationToken);

        var category = CategoryTaxonomy.Resolve(categoryId);
        foreach (var sibling in siblings)
        {
            sibling.CategoryId = categoryId;
            sibling.CategorySource = CategorySource.User;
            sibling.CategoryConfidence = 1;
            sibling.Type = rule.Type ?? CategoryTaxonomy.ImpliedType(category, sibling.Amount);
            sibling.IsRefund = sibling.Amount > 0 && sibling.Type == TransactionType.Expense;
        }
    }
}
