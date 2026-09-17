using FinSight.Infrastructure.Email;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Automation;

/// <summary>Wakes every <see cref="AutomationOptions.TickInterval"/> to run due Gmail scans, then due monthly summary emails.</summary>
internal sealed partial class AutomationWorker(
    AutoScanService scans,
    DigestService digests,
    IOptions<AutomationOptions> options,
    TimeProvider time,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var tick = options.Value.TickInterval < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : options.Value.TickInterval;

        // Let startup (migrations, the job worker) settle before the first run.
        await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
        using var timer = new PeriodicTimer(tick, time);
        do
        {
            try
            {
                await scans.RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, "scans", ex.GetType().Name);
            }

            try
            {
                await digests.RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, "summary emails", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(LogLevel.Warning, "Automation run for {Work} failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string work, string exceptionType);
}
