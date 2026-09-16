using System.Text.Json;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Core.Text;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Pipeline;

public sealed record DiscoveryResult(int MessagesScanned, int NewStatements, int TotalStatements);

/// <summary>Searches Gmail, classifies candidate emails and records likely statements as "discovered".</summary>
public sealed class StatementDiscoveryService(
    FinSightDbContext db,
    GoogleTokenService tokens,
    IGmailClient gmail,
    IOptions<GoogleIntegrationOptions> options,
    TimeProvider time)
{
    private const int FetchConcurrency = 4;

    public async Task<DiscoveryResult> DiscoverAsync(Guid userId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var accessToken = await tokens.GetAccessTokenAsync(userId, cancellationToken);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var messageIds = new List<string>();
        foreach (var query in StatementEmailClassifier.SearchQueries(today.AddMonths(-settings.SearchMonths)))
        {
            var remaining = settings.MaxMessagesPerSync - messageIds.Count;
            if (remaining <= 0)
            {
                break;
            }

            var ids = await gmail.SearchMessageIdsAsync(accessToken, query, remaining, cancellationToken);
            messageIds.AddRange(ids.Where(id => !messageIds.Contains(id)));
        }

        var knownMessages = (await db.Statements
            .Where(s => s.Source == StatementSourceKind.Gmail && s.SourceMessageId != null)
            .Select(s => s.SourceMessageId!)
            .ToListAsync(cancellationToken)).ToHashSet();

        var toFetch = messageIds.Where(id => !knownMessages.Contains(id)).ToList();
        var candidates = new List<EmailCandidate>();

        using var gate = new SemaphoreSlim(FetchConcurrency);
        var fetches = toFetch.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await gmail.GetMessageAsync(accessToken, id, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var candidate in await Task.WhenAll(fetches))
        {
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        var now = time.GetUtcNow();
        var added = 0;

        foreach (var email in candidates)
        {
            var detection = StatementEmailClassifier.Classify(email);
            if (!detection.IsStatement)
            {
                continue;
            }

            foreach (var attachment in detection.PdfAttachments)
            {
                db.Statements.Add(new Statement
                {
                    UserId = userId,
                    Source = StatementSourceKind.Gmail,
                    SourceKey = $"gmail:{email.MessageId}:{attachment.PartId}",
                    SourceMessageId = email.MessageId,
                    SourceThreadId = email.ThreadId,
                    SourcePartId = attachment.PartId,
                    // Subjects and senders can contain names or account digits; store them masked and truncated.
                    Subject = Truncate(SensitiveDataMasker.Mask(email.Subject), 500),
                    Sender = Truncate(email.From, 500),
                    ReceivedAt = email.ReceivedAt,
                    Filename = Truncate(SensitiveDataMasker.Mask(attachment.Filename), 260),
                    SizeBytes = attachment.SizeBytes,
                    DocumentKind = detection.Kind,
                    DetectionConfidence = detection.Confidence,
                    DetectionReasons = JsonSerializer.Serialize(detection.Reasons),
                    Institution = detection.Institution,
                    Status = StatementStatus.Discovered,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                added++;
            }
        }

        var connection = await db.GmailConnections.SingleAsync(c => c.UserId == userId, cancellationToken);
        connection.LastSyncedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var total = await db.Statements.CountAsync(s => s.Source == StatementSourceKind.Gmail, cancellationToken);
        return new DiscoveryResult(toFetch.Count, added, total);
    }

    public async Task<byte[]> DownloadAsync(Guid userId, Statement statement, CancellationToken cancellationToken)
    {
        var accessToken = await tokens.GetAccessTokenAsync(userId, cancellationToken);
        return await gmail.DownloadAttachmentAsync(accessToken, statement.SourceMessageId!, statement.SourcePartId!, cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
