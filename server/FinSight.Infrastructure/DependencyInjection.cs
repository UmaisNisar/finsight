using FinSight.Core.Abstractions;
using FinSight.Infrastructure.Automation;
using FinSight.Infrastructure.Demo;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Pdf;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using FinSight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure;

public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Allows "Try the demo" sign-in with synthetic data. Intended for development and showcases.</summary>
    public bool Enabled { get; set; }

    public int RetentionHours { get; set; } = 24;
}

public static class DependencyInjection
{
    public static IServiceCollection AddFinSightInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GoogleIntegrationOptions>().Bind(configuration.GetSection(GoogleIntegrationOptions.Section));
        services.AddOptions<GeminiOptions>().Bind(configuration.GetSection(GeminiOptions.Section));
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton<DataProtectionFieldProtector>();
        services.AddSingleton<IFieldProtector>(sp => sp.GetRequiredService<DataProtectionFieldProtector>());
        services.AddSingleton<ITokenProtector>(sp => sp.GetRequiredService<DataProtectionFieldProtector>());
        services.AddSingleton<IApiKeyProtector>(sp => sp.GetRequiredService<DataProtectionFieldProtector>());

        services.AddScoped<UserContext>();
        services.AddScoped<IUserContext>(sp => sp.GetRequiredService<UserContext>());

        // SQLite by default; Database:Provider=Postgres for deployments (Persistence/DatabaseSetup.cs).
        services.AddFinSightDatabase(configuration);

        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();

        services.AddHttpClient<GoogleTokenService>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<IGmailClient, GmailApiClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<GeminiClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<GeminiKeyValidator>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<IGeminiKeyResolver, GeminiKeyResolver>();
        services.AddScoped<IGeminiService, GeminiService>();

        // In-memory daily AI allowances and key status; this app runs as a single instance (Gemini/AiUsage.cs).
        services.AddSingleton<AiQuota>();
        services.AddSingleton<AiKeyHealth>();
        services.AddSingleton<PendingCategorizationRetry>();
        services.AddHostedService(sp => sp.GetRequiredService<PendingCategorizationRetry>());

        services.AddSingleton(_ => new JobQueue(configuration.GetValue("Uploads:MaxQueuedMegabytes", JobQueue.DefaultMaxQueuedUploadBytes / (1024 * 1024)) * 1024 * 1024));
        services.AddHostedService<JobWorker>();
        services.AddHostedService<DemoCleanupService>();

        services.AddScoped<JobService>();
        services.AddScoped<JobRunner>();
        services.AddScoped<CategorizationService>();
        services.AddScoped<StatementImportService>();
        services.AddScoped<StatementDiscoveryService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AnalysisService>();
        services.AddScoped<DemoDataService>();

        services.AddFinSightAutomation(configuration);

        return services;
    }
}

internal sealed partial class DemoCleanupService(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<DemoOptions> options, ILogger<DemoCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<DemoDataService>()
                    .DeleteExpiredDemoUsersAsync(TimeSpan.FromHours(options.Value.RetentionHours), stoppingToken);
                if (deleted > 0)
                {
                    LogDeleted(logger, deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(LogLevel.Information, "Deleted {Count} expired demo users")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(LogLevel.Warning, "Demo cleanup failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
