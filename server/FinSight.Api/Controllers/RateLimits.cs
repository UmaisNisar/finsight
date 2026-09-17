using System.Threading.RateLimiting;
using FinSight.Api.Auth;
using FinSight.Api.Middleware;

namespace FinSight.Api.Controllers;

public static class RateLimits
{
    public const string Ai = "ai";
    public const string AiKey = "ai-key";
    public const string Sync = "sync";
    public const string Upload = "upload";
    public const string Demo = "demo";
    public const string TestEmail = "test-email";

    public static IServiceCollection AddFinSightRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // Demo sign-ins create a user with a year of data each, so they are limited per client address.
        var demoPerHour = configuration.GetValue("RateLimits:DemoPerHour", 20);
        var uploadsPerHour = configuration.GetValue("RateLimits:UploadsPerHour", 120);
        var testEmailsPerHour = configuration.GetValue("RateLimits:TestEmailsPerHour", 3);

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

            // Saving a Gemini key calls Google to check it; the limit also stops the endpoint being used to test stolen keys.
            options.AddPolicy(AiKey, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
            }));

            options.AddPolicy(Sync, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(10),
            }));

            // Some banks (CIBC) only offer one PDF per month, so a year across a few accounts is dozens of uploads in a row.
            // Each file is also capped in size, and StatementsController limits how many uploads can wait to be processed.
            options.AddPolicy(Upload, context => RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(context), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = uploadsPerHour,
                Window = TimeSpan.FromHours(1),
                SegmentsPerWindow = 6,
            }));

            // A test summary sends a real email to the user's own address; a few an hour is plenty and limits mail volume.
            options.AddPolicy(TestEmail, context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = testEmailsPerHour,
                Window = TimeSpan.FromHours(1),
            }));

            options.AddPolicy(Demo, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = demoPerHour,
                    Window = TimeSpan.FromHours(1),
                }));
        });

        return services;
    }

    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirst(FinSightClaims.UserId)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}
