using System.Threading.RateLimiting;
using FinSight.Api.Auth;
using FinSight.Api.Middleware;

namespace FinSight.Api.Controllers;

public static class RateLimits
{
    public const string Ai = "ai";
    public const string Sync = "sync";
    public const string Upload = "upload";
    public const string Demo = "demo";

    public static IServiceCollection AddFinSightRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
                await context.HttpContext.Response.WriteAsJsonAsync(
                    ApiErrors.Create(429, "rate_limited", "You're doing that too often. Wait a moment and try again."), cancellationToken);

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                }));

            options.AddPolicy(Ai, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
            }));

            options.AddPolicy(Sync, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(10),
            }));

            options.AddPolicy(Upload, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 40,
                Window = TimeSpan.FromHours(1),
            }));

            options.AddPolicy(Demo, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromHours(1),
                }));
        });

        return services;
    }

    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirst(FinSightClaims.UserId)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}
