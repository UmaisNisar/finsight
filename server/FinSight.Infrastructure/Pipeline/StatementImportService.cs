using System.Security.Cryptography;
using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Domain;
using FinSight.Core.Normalization;
using FinSight.Core.Parsing;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Infrastructure.Pipeline;

public enum ImportOutcome
{
    Imported,
    DuplicateFile,
    Failed,
}

public sealed record ImportResult(ImportOutcome Outcome, int TransactionCount, int SkippedDuplicates, string? FailureCode);

/// <summary>
/// PDF extraction → parsing → normalization → duplicate detection → rule categorization, for one
/// statement. Idempotent: reprocessing replaces the statement's transactions while keeping user
/// edits, and transactions already imported from an overlapping statement are skipped.
/// </summary>
public sealed class StatementImportService(
    FinSightDbContext db,
    IPdfTextExtractor extractor,
    CategorizationService categorization,
    TimeProvider time)
{
    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task<ImportResult> ImportAsync(Statement statement, byte[] pdf, string defaultCurrency, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hash = HashOf(pdf);

        var duplicateOf = await db.Statements
            .Where(s => s.Id != statement.Id && s.ContentHash == hash && s.Status == StatementStatus.Processed)
            .Select(s => s.Filename)
            .FirstOrDefaultAsync(cancellationToken);

        statement.ContentHash = hash;
        statement.SizeBytes = pdf.Length;
        statement.UpdatedAt = now;

        if (duplicateOf is not null)
        {
            statement.Status = StatementStatus.Processed;
            statement.FailureCode = null;
            statement.TransactionCount = 0;
            statement.ProcessedAt = now;
            statement.ExtractionWarnings = JsonSerializer.Serialize(new[] { $"This is the same file as \"{duplicateOf}\", which is already imported." });
            await db.SaveChangesAsync(cancellationToken);
            return new ImportResult(ImportOutcome.DuplicateFile, 0, 0, null);
        }

        statement.Status = StatementStatus.Processing;
        await db.SaveChangesAsync(cancellationToken);

        PdfTextDocument text;
        try
        {
            text = extractor.Extract(pdf);
        }
        catch (PdfPasswordRequiredException)
        {
            return await FailAsync(statement, StatementFailure.PasswordProtected, cancellationToken);
        }
        catch (PdfUnreadableException)
        {
            return await FailAsync(statement, StatementFailure.Unreadable, cancellationToken);
        }

        var referenceDate = DateOnly.FromDateTime((statement.ReceivedAt ?? now).UtcDateTime);
        var parsed = StatementParser.Parse(text, new StatementParseContext(
            statement.Currency ?? defaultCurrency,
            referenceDate,
            statement.Sender is null ? null : Core.Statements.StatementEmailClassifier.ExtractAddress(statement.Sender),
            statement.Subject));

        if (parsed.Failure != ParseFailure.None)
        {
            statement.ExtractionWarnings = JsonSerializer.Serialize(parsed.Warnings);
            return await FailAsync(statement, parsed.Failure == ParseFailure.NoTextLayer ? StatementFailure.NoTextLayer : StatementFailure.NoTransactions, cancellationToken);
        }

        var metadata = parsed.Metadata;
        statement.Institution = metadata.Institution ?? statement.Institution;
        statement.AccountType = metadata.AccountType;
        statement.AccountMask = metadata.AccountMask;
        statement.Currency = metadata.Currency;
        statement.PeriodStart = metadata.PeriodStart;
        statement.PeriodEnd = metadata.PeriodEnd;
        statement.OpeningBalance = metadata.OpeningBalance;
        statement.ClosingBalance = metadata.ClosingBalance;
        statement.ExtractionConfidence = parsed.Confidence;
        if (metadata.AccountType == AccountType.CreditCard)
        {
            statement.DocumentKind = DocumentKind.CreditCardStatement;
        }
        else if (statement.DocumentKind == DocumentKind.Unknown)
        {
            statement.DocumentKind = DocumentKind.BankStatement;
        }

        var accountKey = TransactionNormalizer.AccountKey(statement.Institution, statement.AccountType, statement.AccountMask);
        var normalized = TransactionNormalizer.Normalize(parsed, accountKey);

        await using var dbTransaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Keep user edits from a previous import of this statement, keyed by fingerprint.
        var previous = await db.Transactions.Where(t => t.StatementId == statement.Id).ToListAsync(cancellationToken);
        var overrides = previous.ToDictionary(t => t.Fingerprint);
        db.Transactions.RemoveRange(previous);
        await db.SaveChangesAsync(cancellationToken);

        var fingerprints = normalized.Select(n => n.Fingerprint).ToList();
        var importedElsewhere = (await db.Transactions
            .Where(t => fingerprints.Contains(t.Fingerprint))
            .Select(t => t.Fingerprint)
            .ToListAsync(cancellationToken)).ToHashSet();

        var created = new List<Transaction>();
        foreach (var item in normalized.Where(n => !importedElsewhere.Contains(n.Fingerprint)))
        {
            var transaction = new Transaction
            {
                UserId = statement.UserId,
                StatementId = statement.Id,
                Fingerprint = item.Fingerprint,
                Date = item.Date,
                PostingDate = item.PostingDate,
                Description = item.Description,
                Amount = item.Amount,
                Balance = item.Balance,
                Currency = item.Currency,
                ExtractionConfidence = item.ExtractionConfidence,
                Merchant = item.Merchant.Display,
                MerchantKey = item.Merchant.Key,
                CategoryId = Core.Categories.CategoryTaxonomy.Uncategorized,
                IsReversal = item.IsReversal,
                CreatedAt = now,
            };

            if (overrides.TryGetValue(item.Fingerprint, out var old))
            {
                transaction.UserCategoryId = old.UserCategoryId;
                transaction.UserMerchant = old.UserMerchant;
                transaction.UserType = old.UserType;
                transaction.IsExcluded = old.IsExcluded;
            }

            created.Add(transaction);
        }

        await categorization.ApplyRulesAsync(created, statement.AccountType, cancellationToken);
        db.Transactions.AddRange(created);

        var skipped = normalized.Count - created.Count;
        var warnings = parsed.Warnings.ToList();
        if (skipped > 0)
        {
            warnings.Add($"{skipped} transaction(s) were already imported from another statement and were skipped.");
        }

        statement.Status = StatementStatus.Processed;
        statement.FailureCode = null;
        statement.TransactionCount = created.Count;
        statement.ExtractionWarnings = JsonSerializer.Serialize(warnings);
        statement.ProcessedAt = now;
        statement.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);

        return new ImportResult(ImportOutcome.Imported, created.Count, skipped, null);
    }

    public async Task<ImportResult> FailAsync(Statement statement, string code, CancellationToken cancellationToken)
    {
        statement.Status = StatementStatus.Failed;
        statement.FailureCode = code;
        statement.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(ImportOutcome.Failed, 0, 0, code);
    }
}
