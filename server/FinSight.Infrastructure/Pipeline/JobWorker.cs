using System.Collections.Concurrent;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Pipeline;

/// <summary>Runs queued jobs in the background, one at a time per user, a few users in parallel.</summary>
public sealed partial class JobWorker(JobQueue queue, IServiceScopeFactory scopes, TimeProvider time, ILogger<JobWorker> logger) : BackgroundService
{
    private const int Concurrency = 2;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Jobs interrupted by a restart can never finish; mark them failed so the UI does not spin forever.
        await using var scope = scopes.CreateAsyncScope();
        var userContext = scope.ServiceProvider.GetRequiredService<UserContext>();
        var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
        using (userContext.BeginSystemScope())
        {
            await db.ProcessingJobs
                .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.ErrorCode, "interrupted")
                    .SetProperty(j => j.CompletedAt, (DateTimeOffset?)time.GetUtcNow()), cancellationToken);

            await db.Statements
                .Where(s => s.Status == StatementStatus.Downloading || s.Status == StatementStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StatementStatus.Failed)
                    .SetProperty(x => x.FailureCode, StatementFailure.Unexpected), cancellationToken);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var userLock = _userLocks.GetOrAdd(item.UserId, _ => new SemaphoreSlim(1, 1));
            await userLock.WaitAsync(stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(item.UserId);
                await scope.ServiceProvider.GetRequiredService<JobRunner>().RunAsync(item, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUnhandled(logger, ex.GetType().Name, item.JobId);
            }
            finally
            {
                userLock.Release();
                queue.ReleaseUploadBytes(JobQueue.UploadBytesOf(item));
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Unhandled {ExceptionType} while running job {JobId}")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, Guid jobId);
}
