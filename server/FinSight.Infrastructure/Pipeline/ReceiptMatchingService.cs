using System.Globalization;
using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Domain;
using FinSight.Core.Receipts;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Pipeline;

public sealed record ReceiptMatchResult(int Considered, int Matched);

/// <summary>
/// After a scan, finds the order-confirmation email for a purchase and records what was bought. Each transaction with
/// a merchant FinSight knows how to find (IKEA, Amazon, Uber…) is searched in Gmail by sender and a date window around
/// the charge; the best match's body is downloaded, stripped of personal details, and read by the AI into item lines.
/// The email is never stored — only the items, order number and subject, encrypted like any other transaction detail.
/// </summary>
public sealed partial class ReceiptMatchingService(
    FinSightDbContext db,
    GoogleTokenService tokens,
    IGmailClient gmail,
    IGeminiService gemini,
    ILogger<ReceiptMatchingService> logger)
{
    /// <summary>
    /// Order confirmations arrive around the charge, usually before it: the email fires when the order is placed, but a
    /// card charge posts days later (it ships, or the bank batches it). So the window leans back further than it leans forward.
    /// </summary>
    private static readonly int DaysBefore = 10;
    private static readonly int DaysAfter = 3;

    /// <summary>At most this many purchases are matched per scan, so one scan can't fan out into thousands of Gmail reads.</summary>
    private const int MaxPerRun = 100;

    /// <summary>Candidate emails fetched per purchase before picking the best one.</summary>
    private const int CandidatesPerTransaction = 5;

    public async Task<ReceiptMatchResult> MatchAsync(Guid userId, IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken)
    {
        var eligible = transactions
            .Where(t => t.EffectiveType == TransactionType.Expense && ReceiptSenders.Knows(t.EffectiveMerchant))
            .ToList();
        if (eligible.Count == 0)
        {
            return new ReceiptMatchResult(0, 0);
        }

        var already = (await db.TransactionReceipts
            .Where(r => eligible.Select(t => t.Id).Contains(r.TransactionId))
            .Select(r => r.TransactionId)
            .ToListAsync(cancellationToken)).ToHashSet();

        // Cap AFTER dropping already-matched purchases, so each scan makes progress through new ones instead of
        // re-chewing the same first MaxPerRun every time. Newest first: recent purchases are the ones a user looks at.
        var toMatch = eligible
            .Where(t => !already.Contains(t.Id))
            .OrderByDescending(t => t.Date)
            .Take(MaxPerRun)
            .ToList();
        if (toMatch.Count == 0)
        {
            return new ReceiptMatchResult(0, 0);
        }

        string accessToken;
        try
        {
            accessToken = await tokens.GetAccessTokenAsync(userId, cancellationToken);
        }
        catch (GmailAuthExpiredException)
        {
            // No Gmail connection: nothing to match against. The scan that called us already surfaced the reconnect.
            return new ReceiptMatchResult(toMatch.Count, 0);
        }

        var matched = 0;
        var aiAvailable = true;
        foreach (var transaction in toMatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (receipt, aiStillAvailable) = await MatchOneAsync(userId, accessToken, transaction, aiAvailable, cancellationToken);
                aiAvailable = aiStillAvailable;
                if (receipt is not null)
                {
                    db.TransactionReceipts.Add(receipt);
                    matched++;
                }
            }
            catch (GmailAuthExpiredException)
            {
                break; // The token died mid-run; stop rather than hammer a dead connection.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One receipt failing must never fail the scan; the purchase simply has no receipt attached.
                LogMatchFailed(logger, transaction.Id, ex);
            }
        }

        if (matched > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ReceiptMatchResult(toMatch.Count, matched);
    }

    private async Task<(TransactionReceipt? Receipt, bool AiAvailable)> MatchOneAsync(Guid userId, string accessToken, Transaction transaction, bool aiAvailable, CancellationToken cancellationToken)
    {
        var sender = ReceiptSenders.For(transaction.EffectiveMerchant)!;
        var from = string.Join(" OR ", sender.Domains);
        var after = transaction.Date.AddDays(-DaysBefore).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var before = transaction.Date.AddDays(DaysAfter + 1).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var query = $"from:({from}) after:{after} before:{before}";

        var ids = await gmail.SearchMessageIdsAsync(accessToken, query, CandidatesPerTransaction, cancellationToken);
        if (ids.Count == 0)
        {
            return (null, aiAvailable);
        }

        var candidates = new List<EmailCandidate>();
        foreach (var id in ids)
        {
            try
            {
                candidates.Add(await gmail.GetMessageAsync(accessToken, id, cancellationToken));
            }
            catch (FileNotFoundException)
            {
                // Deleted between search and fetch; skip it.
            }
        }

        var best = PickBest(candidates, transaction);
        if (best is null)
        {
            return (null, aiAvailable);
        }

        var receipt = new TransactionReceipt
        {
            UserId = userId,
            TransactionId = transaction.Id,
            MessageId = best.MessageId,
            Subject = best.Subject,
            EmailDate = DateOnly.FromDateTime(best.ReceivedAt.UtcDateTime),
            Source = ReceiptSource.LinkOnly,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Items need the AI. If it's off or over its limit, keep the match as a link to the email so the user still
        // gets "here's the order confirmation", just without the itemised list.
        if (aiAvailable)
        {
            try
            {
                var body = await gmail.GetMessageBodyAsync(accessToken, best.MessageId, cancellationToken);
                var redacted = ReceiptRedactor.Redact(body);
                if (redacted.Length > 0)
                {
                    var extraction = await gemini.ExtractReceiptAsync(transaction.EffectiveMerchant, redacted, cancellationToken);
                    receipt.Source = ReceiptSource.Ai;
                    receipt.OrderNumber = extraction.OrderNumber ?? "";
                    receipt.Total = extraction.Total;
                    receipt.ItemsJson = JsonSerializer.Serialize(extraction.Items);
                }
            }
            catch (AiUnavailableException)
            {
                aiAvailable = false; // Don't try the AI again for the rest of this run; keep the remaining matches link-only.
            }
        }

        return (receipt, aiAvailable);
    }

    /// <summary>
    /// The candidate whose email most likely belongs to this purchase: one that mentions the exact amount wins, otherwise
    /// the one closest in time to the charge. Avoids attaching a shipping or marketing email that merely shares the sender.
    /// </summary>
    private static EmailCandidate? PickBest(List<EmailCandidate> candidates, Transaction transaction)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var amount = Math.Abs(transaction.Amount);
        var amountText = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var charge = transaction.Date.ToDateTime(TimeOnly.MinValue);

        return candidates
            .OrderByDescending(c => (c.Subject + " " + c.Snippet).Contains(amountText, StringComparison.Ordinal))
            .ThenBy(c => Math.Abs((c.ReceivedAt.UtcDateTime - charge).TotalDays))
            .First();
    }

    [LoggerMessage(LogLevel.Debug, "Receipt match failed for transaction {TransactionId}")]
    private static partial void LogMatchFailed(ILogger logger, Guid transactionId, Exception ex);
}
