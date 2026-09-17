using System.Globalization;
using FinSight.Core.Abstractions;
using FinSight.Core.Analytics;
using FinSight.Core.Domain;
using FinSight.Core.Import;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Pipeline;

public static class StatementLabels
{
    public static string Title(Statement statement)
    {
        var culture = CultureInfo.InvariantCulture;

        // A CSV or OFX download covers whatever dates the user picked, not a statement month: "CSV import · Aug 1 – Aug 31, 2026",
        // or just the dates when the bank is known (it is shown beside the title).
        if (statement.Format is StatementFileFormat.Csv or StatementFileFormat.Ofx or StatementFileFormat.Qfx
            && statement.PeriodStart is { } from && statement.PeriodEnd is { } to)
        {
            var range = DateRange(from, to);
            return statement.Institution is null ? $"{StatementFileParsers.DisplayName(statement.Format.Value)} import · {range}" : range;
        }

        if (statement.PeriodEnd is { } end)
        {
            return end.ToString("MMMM yyyy", culture);
        }

        if (StatementAlert.IsAlert(statement))
        {
            return AlertTitle(statement);
        }

        return statement.ReceivedAt is { } received
            ? $"Received {received.ToString("MMM d, yyyy", culture)}"
            : statement.Filename;
    }

    /// <summary>"Aug 1 – Aug 31, 2026", "Dec 3, 2025 – Jan 2, 2026" or "Aug 15, 2026".</summary>
    public static string DateRange(DateOnly from, DateOnly to)
    {
        var culture = CultureInfo.InvariantCulture;
        if (from == to)
        {
            return to.ToString("MMM d, yyyy", culture);
        }

        return from.Year == to.Year
            ? $"{from.ToString("MMM d", culture)} – {to.ToString("MMM d, yyyy", culture)}"
            : $"{from.ToString("MMM d, yyyy", culture)} – {to.ToString("MMM d, yyyy", culture)}";
    }

    /// <summary>"CIBC credit card ending 5190", falling back to "CIBC statement", "Credit card ending 5190" or "Statement".</summary>
    public static string AlertTitle(Statement statement)
    {
        var account = AccountLabel(statement.AccountType) ?? (statement.AccountMask is null ? "statement" : "account");
        var title = statement.Institution is null ? account : $"{statement.Institution} {account}";
        if (statement.AccountMask is not null)
        {
            title += $" ending {statement.AccountMask}";
        }

        return char.ToUpperInvariant(title[0]) + title[1..];
    }

    public static string? AccountLabel(AccountType type) => type switch
    {
        AccountType.CreditCard => "credit card",
        AccountType.Chequing => "chequing account",
        AccountType.Savings => "savings account",
        AccountType.LineOfCredit => "line of credit",
        AccountType.Investment => "investment account",
        _ => null,
    };

    public static string StepLabel(Statement statement) =>
        statement.Institution is null || StatementAlert.IsAlert(statement) && statement.PeriodEnd is null
            ? Title(statement)
            : $"{Title(statement)} · {statement.Institution}";

    /// <summary>"Found 3 new statements and 2 statement alerts". Alerts are emails saying a statement is ready without attaching it.</summary>
    public static string SyncDetail(DiscoveryResult result)
    {
        if (result.NewStatements == 0 && result.NewAlerts == 0)
        {
            return result.TotalStatements == 0 ? "No statements found" : "No new statements";
        }

        var parts = new List<string>();
        if (result.NewStatements > 0)
        {
            parts.Add(Plural(result.NewStatements, "new statement"));
        }

        if (result.NewAlerts > 0)
        {
            parts.Add(Plural(result.NewAlerts, "statement alert"));
        }

        return $"Found {string.Join(" and ", parts)}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static IEnumerable<JobStep> ProcessingPlan(IEnumerable<Statement> statements) =>
        statements.Select(s => new JobStep($"s:{s.Id}", StepLabel(s), StepStatus.Pending))
            .Append(new JobStep("categorize", "Categorizing transactions", StepStatus.Pending))
            .Append(new JobStep("insights", "Generating insights", StepStatus.Pending));

    public static IEnumerable<JobStep> SyncPlan() =>
    [
        new("gmail", "Connecting to Gmail", StepStatus.Pending),
        new("search", "Finding statements", StepStatus.Pending),
    ];
}

/// <summary>Executes queued jobs: Gmail discovery, and the statement processing pipeline.</summary>
public sealed partial class JobRunner(
    FinSightDbContext db,
    StatementDiscoveryService discovery,
    StatementImportService importer,
    CategorizationService categorization,
    AnalysisService analysis,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    public async Task RunAsync(JobWorkItem item, CancellationToken cancellationToken)
    {
        var reporter = new JobReporter(db, item.JobId, time);
        await reporter.LoadAsync(cancellationToken);
        await reporter.StartAsync(cancellationToken);

        try
        {
            if (item.Kind == JobKind.Sync)
            {
                await RunSyncAsync(item, reporter, cancellationToken);
            }
            else
            {
                await RunProcessingAsync(item, reporter, cancellationToken);
            }

            await reporter.CompleteAsync(cancellationToken);
        }
        catch (GmailAuthExpiredException)
        {
            await reporter.FailAsync(StatementFailure.GmailAuthExpired, CancellationToken.None);
        }
        catch (GmailNotConnectedException)
        {
            await reporter.FailAsync(StatementFailure.GmailNotConnected, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can include request data; log the type and job id only.
            LogJobFailed(logger, ex.GetType().Name, item.JobId);
            await reporter.FailAsync(StatementFailure.Unexpected, CancellationToken.None);
        }
    }

    private async Task RunSyncAsync(JobWorkItem item, JobReporter reporter, CancellationToken cancellationToken)
    {
        await reporter.SetAsync("gmail", "Connecting to Gmail", StepStatus.Running, cancellationToken: cancellationToken);
        await reporter.SetAsync("search", "Finding statements", StepStatus.Running, cancellationToken: cancellationToken);
        await reporter.SetAsync("gmail", "Gmail connected", StepStatus.Done, cancellationToken: cancellationToken);

        var result = await discovery.DiscoverAsync(item.UserId, cancellationToken);
        await reporter.SetAsync("search", StatementLabels.SyncDetail(result), StepStatus.Done, cancellationToken: cancellationToken);
    }

    private async Task RunProcessingAsync(JobWorkItem item, JobReporter reporter, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(cancellationToken);
        var statements = await db.Statements.Where(s => item.StatementIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var imported = new List<Statement>();

        foreach (var statement in statements.OrderByDescending(s => s.PeriodEnd ?? DateOnly.FromDateTime((s.ReceivedAt ?? s.CreatedAt).UtcDateTime)))
        {
            var key = $"s:{statement.Id}";
            var label = StatementLabels.StepLabel(statement);

            try
            {
                var file = await ObtainFileAsync(item, statement, reporter, key, label, cancellationToken);
                if (file is null)
                {
                    await reporter.FailStepAsync(key, label, statement.FailureCode, cancellationToken);
                    continue;
                }

                await reporter.SetAsync(key, label, StepStatus.Running, "Reading statement", cancellationToken);
                var result = await importer.ImportAsync(statement, file.Content, user.Settings.Currency, cancellationToken, file.Password);

                label = StatementLabels.StepLabel(statement);
                switch (result.Outcome)
                {
                    case ImportOutcome.Imported:
                        imported.Add(statement);
                        var detail = $"{result.TransactionCount} transaction{(result.TransactionCount == 1 ? "" : "s")}";

                        // Uploading the statement a statement alert asked for clears that alert.
                        if (item.Kind == JobKind.Upload && statement.Source == StatementSourceKind.ManualUpload
                            && await importer.FulfilAlertAsync(statement, cancellationToken) is not null)
                        {
                            detail += " · statement alert cleared";
                        }

                        await reporter.SetAsync(key, label, StepStatus.Done, detail, cancellationToken);
                        break;
                    case ImportOutcome.DuplicateFile:
                        await reporter.SetAsync(key, label, StepStatus.Skipped, "Already imported", cancellationToken);
                        break;
                    default:
                        await reporter.FailStepAsync(key, label, result.FailureCode, cancellationToken);
                        break;
                }
            }
            catch (GmailAuthExpiredException)
            {
                await importer.FailAsync(statement, StatementFailure.GmailAuthExpired, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not GmailNotConnectedException)
            {
                LogStatementFailed(logger, ex.GetType().Name, statement.Id);
                db.ChangeTracker.Clear();

                // The statement may have been deleted while it was processing; then there is nothing to mark.
                var fresh = await db.Statements.SingleOrDefaultAsync(s => s.Id == statement.Id, CancellationToken.None);
                if (fresh is null)
                {
                    await reporter.SetAsync(key, label, StepStatus.Skipped, "Statement was deleted", CancellationToken.None);
                    continue;
                }

                await importer.FailAsync(fresh, StatementFailure.Unexpected, CancellationToken.None);
                await reporter.FailStepAsync(key, label, StatementFailure.Unexpected, CancellationToken.None);
            }
        }

        await CategorizeAsync(user, imported, reporter, cancellationToken);
        await GenerateInsightsAsync(user, imported, reporter, cancellationToken);
    }

    private async Task<UploadedFile?> ObtainFileAsync(JobWorkItem item, Statement statement, JobReporter reporter, string key, string label, CancellationToken cancellationToken)
    {
        if (item.Uploads?.TryGetValue(statement.Id, out var uploaded) == true)
        {
            return uploaded;
        }

        // Uploaded statements, and alerts that never had an attachment, can only be processed from an uploaded file.
        if (statement.Source != StatementSourceKind.Gmail || StatementAlert.IsAlert(statement))
        {
            await importer.FailAsync(statement, StatementFailure.UploadRequired, cancellationToken);
            return null;
        }

        await reporter.SetAsync(key, label, StepStatus.Running, "Downloading", cancellationToken);
        statement.Status = StatementStatus.Downloading;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            return new UploadedFile(await discovery.DownloadAsync(item.UserId, statement, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            await importer.FailAsync(statement, StatementFailure.AttachmentMissing, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await importer.FailAsync(statement, StatementFailure.DownloadFailed, cancellationToken);
        }

        return null;
    }

    private async Task CategorizeAsync(User user, List<Statement> imported, JobReporter reporter, CancellationToken cancellationToken)
    {
        if (imported.Count == 0)
        {
            await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Skipped, "Nothing new to categorize", cancellationToken);
            return;
        }

        await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Running, cancellationToken: cancellationToken);

        var detail = "Categorized with rules";
        if (user.Settings.AiCategorizationEnabled)
        {
            var outcome = await categorization.CategorizeWithAiAsync(imported.Select(s => s.Id).ToList(), cancellationToken);
            detail = outcome.Failure switch
            {
                AiFailure.NotConfigured => "Categorized with rules (AI not configured)",
                not null => "Categorized with rules (AI temporarily unavailable)",
                _ when outcome.MerchantsCategorized > 0 => $"AI categorized {outcome.MerchantsCategorized} merchant{(outcome.MerchantsCategorized == 1 ? "" : "s")}",
                _ => detail,
            };
        }

        var dates = await db.Transactions.Where(t => imported.Select(s => s.Id).Contains(t.StatementId))
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => t.Date), Max = g.Max(t => t.Date) })
            .FirstOrDefaultAsync(cancellationToken);

        if (dates is not null)
        {
            var pairs = await categorization.MatchTransfersAsync(dates.Min, dates.Max, cancellationToken);
            if (pairs > 0)
            {
                detail += $" · {pairs} transfer{(pairs == 1 ? "" : "s")} matched";
            }
        }

        await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Done, detail, cancellationToken);
    }

    private async Task GenerateInsightsAsync(User user, List<Statement> imported, JobReporter reporter, CancellationToken cancellationToken)
    {
        // Without Gemini (no key, key refused, quota or limit used up) the analysis is written by FinSight instead (AnalysisService).
        if (imported.Count == 0 || !user.Settings.AiInsightsEnabled)
        {
            var reason = imported.Count == 0 ? "No new data" : "AI insights are turned off";
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, reason, cancellationToken);
            return;
        }

        await reporter.SetAsync("insights", "Generating insights", StepStatus.Running, cancellationToken: cancellationToken);

        // The overview opens on last month, so analyze that when it has data; otherwise the latest month that does.
        var latest = await db.Transactions.MaxAsync(t => (DateOnly?)t.Date, cancellationToken);
        if (latest is null)
        {
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, "No transactions", cancellationToken);
            return;
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var range = PeriodResolver.Resolve(PeriodPreset.LastMonth, today);
        if (!await db.Transactions.AnyAsync(t => t.Date >= range.Start && t.Date <= range.End, cancellationToken))
        {
            var month = new DateOnly(latest.Value.Year, latest.Value.Month, 1);
            range = new DateRange(month, month.AddMonths(1).AddDays(-1));
        }

        try
        {
            var stored = await analysis.GenerateAsync(range, cancellationToken);
            var detail = stored.Source == AnalysisSource.BuiltIn ? $"{range.Label()} · written without AI" : range.Label();
            await reporter.SetAsync("insights", "Insights ready", StepStatus.Done, detail, cancellationToken);
        }
        catch (Exception ex) when (ex is AiUnavailableException or AnalysisUnavailableException)
        {
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, "AI analysis is temporarily unavailable", cancellationToken);
        }
    }

    [LoggerMessage(LogLevel.Error, "Job {JobId} failed with {ExceptionType}")]
    private static partial void LogJobFailed(ILogger logger, string exceptionType, Guid jobId);

    [LoggerMessage(LogLevel.Error, "Statement {StatementId} failed with {ExceptionType}")]
    private static partial void LogStatementFailed(ILogger logger, string exceptionType, Guid statementId);
}
