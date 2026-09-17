using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinSight.Api.Hosting;

/// <summary>
/// Probes for container orchestrators and load balancers. <c>/health/live</c> answers while the process can serve requests;
/// <c>/health/ready</c> also checks that the database accepts connections. Both are anonymous, exempt from rate limits,
/// and answer only <c>Healthy</c> or <c>Unhealthy</c>: no versions, hosts or error details.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    private const string ReadyTag = "ready";

    public static IServiceCollection AddFinSightHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapFinSightHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous()
            .DisableRateLimiting();

        return endpoints;
    }

    private sealed class DatabaseHealthCheck(FinSightDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception can carry the host or user name from the connection string, so it is not reported.
                return HealthCheckResult.Unhealthy();
            }
        }
    }
}
