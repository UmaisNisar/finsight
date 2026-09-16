using System.Text.Json;
using System.Threading.Channels;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Infrastructure.Pipeline;

/// <param name="Uploads">PDF bytes for manual uploads, held in memory only for the life of the job.</param>
public sealed record JobWorkItem(Guid JobId, Guid UserId, JobKind Kind, IReadOnlyList<Guid> StatementIds, IReadOnlyDictionary<Guid, byte[]>? Uploads = null);

/// <summary>In-process work queue. Job state is persisted, so the UI can poll it; payloads are not.</summary>
public sealed class JobQueue
{
    private readonly Channel<JobWorkItem> _channel = Channel.CreateUnbounded<JobWorkItem>(new UnboundedChannelOptions { SingleReader = false });

    public ChannelReader<JobWorkItem> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(JobWorkItem item, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(item, cancellationToken);
}

public static class JobSteps
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<JobStep> Read(string json) =>
        JsonSerializer.Deserialize<List<JobStep>>(json, Json) ?? [];
}

public sealed class JobService(FinSightDbContext db, JobQueue queue, TimeProvider time)
{
    public async Task<ProcessingJob> CreateAsync(Guid userId, JobKind kind, IEnumerable<JobStep> initialSteps, CancellationToken cancellationToken)
    {
        var job = new ProcessingJob
        {
            UserId = userId,
            Kind = kind,
            Status = JobStatus.Queued,
            StepsJson = JsonSerializer.Serialize(initialSteps, JobSteps.Json),
            CreatedAt = time.GetUtcNow(),
        };
        db.ProcessingJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public ValueTask EnqueueAsync(JobWorkItem item, CancellationToken cancellationToken) => queue.EnqueueAsync(item, cancellationToken);

    public Task<ProcessingJob?> FindAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.ProcessingJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    public Task<ProcessingJob?> LatestActiveAsync(CancellationToken cancellationToken) =>
        db.ProcessingJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Records step-by-step progress. Writes directly so progress is visible while the pipeline's own changes are pending.</summary>
public sealed class JobReporter(FinSightDbContext db, Guid jobId, TimeProvider time)
{
    private readonly List<JobStep> _steps = [];

    public IReadOnlyList<JobStep> Steps => _steps;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var json = await db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.StepsJson).SingleAsync(cancellationToken);
        _steps.AddRange(JobSteps.Read(json));
    }

    public Task SetAsync(string key, string label, StepStatus status, string? detail = null, CancellationToken cancellationToken = default)
    {
        var index = _steps.FindIndex(s => s.Key == key);
        var step = new JobStep(key, label, status, detail);
        if (index >= 0)
        {
            _steps[index] = step;
        }
        else
        {
            _steps.Add(step);
        }

        return FlushAsync(null, null, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => FlushAsync(JobStatus.Running, null, cancellationToken);

    public Task CompleteAsync(CancellationToken cancellationToken) => FlushAsync(JobStatus.Succeeded, null, cancellationToken);

    public Task FailAsync(string errorCode, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].Status is StepStatus.Pending or StepStatus.Running)
            {
                _steps[i] = _steps[i] with { Status = StepStatus.Skipped };
            }
        }

        return FlushAsync(JobStatus.Failed, errorCode, cancellationToken);
    }

    private async Task FlushAsync(JobStatus? status, string? errorCode, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(_steps, JobSteps.Json);
        var terminal = status is JobStatus.Succeeded or JobStatus.Failed;
        var now = time.GetUtcNow();

        if (status is null)
        {
            await db.ProcessingJobs.Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.StepsJson, json), cancellationToken);
            return;
        }

        await db.ProcessingJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.StepsJson, json)
                .SetProperty(j => j.Status, status.Value)
                .SetProperty(j => j.ErrorCode, errorCode)
                .SetProperty(j => j.CompletedAt, terminal ? now : null), cancellationToken);
    }
}
