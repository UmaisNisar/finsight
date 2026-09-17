using System.Text.Json;
using System.Text.Json.Serialization;
using FinSight.Api.Auth;
using FinSight.Api.Controllers;
using FinSight.Api.Hosting;
using FinSight.Api.Middleware;
using FinSight.Infrastructure;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Secrets (Google client secret, Gemini API key) come from user secrets in development and
// environment variables in production. appsettings.Local.json is git-ignored for convenience.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// SQLite by default; Database:Provider=Postgres with a PostgreSQL connection string for deployments.
// A relative SQLite path resolves against the app's content root, not whatever directory it was started from,
// so the database and the Data Protection keys always live side by side.
var databaseOptions = builder.Configuration.GetDatabaseOptions();
if (databaseOptions.Provider == DatabaseProvider.Sqlite)
{
    builder.Configuration["ConnectionStrings:FinSight"] = SqlitePaths.Resolve(
        builder.Configuration.GetConnectionString("FinSight") ?? DatabaseSetup.DefaultSqliteConnectionString, builder.Environment.ContentRootPath);
}

builder.Services.AddFinSightInfrastructure(builder.Configuration);

// The key ring is encrypted with DataProtection:CertificatePath, or DPAPI on Windows. Outside Development, startup fails
// rather than write an unencrypted key ring, unless DataProtection:AllowUnprotectedKeys is set (Hosting/DataProtectionSetup.cs).
var keyRingProtection = builder.Services.AddFinSightDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddFinSightHealthChecks();

builder.Services
    .AddControllers(options =>
    {
        // JSON bodies are small (settings, edits, lists of ids). Kestrel's default allows 30 MB per request; statement
        // uploads set their own, larger limit.
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxJsonRequestBytes));
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = _ =>
            ApiErrors.BadRequest("invalid_request", "Some of the information sent wasn't valid.");
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddFinSightAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddFinSightRateLimiting(builder.Configuration);
builder.Services.AddOpenApi();

// X-Forwarded-* headers are only honoured behind a reverse proxy the operator opts into. Trusting them from any
// client would let callers spoof their IP (defeating per-IP rate limits) and the scheme used for secure cookies.
var trustForwardedHeaders = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;

        // Hosting platforms often proxy from addresses that aren't known ahead of time; list them when they are.
        var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        if (knownProxies.Length == 0)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }

        foreach (var proxy in knownProxies)
        {
            options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        }
    });
}

var app = builder.Build();

if (keyRingProtection == KeyRingProtection.None && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("DataProtection:AllowUnprotectedKeys is set: the key ring is stored unencrypted. Configure DataProtection:CertificatePath for real deployments.");
}

// Migrations run on startup unless Database:MigrateOnStartup is false. On PostgreSQL an advisory lock serializes instances that
// start together. Database:MigrateOnly applies them and exits, for a separate release step.
if (databaseOptions.MigrateOnStartup || databaseOptions.MigrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseSetup.MigrateAsync(scope.ServiceProvider.GetRequiredService<FinSightDbContext>());
}

if (databaseOptions.MigrateOnly)
{
    app.Logger.LogInformation("Database migrations applied; exiting because Database:MigrateOnly is set.");
    return;
}

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<UserContextMiddleware>();
app.UseMiddleware<CsrfHeaderMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapFinSightHealthChecks();
app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.Json(ApiErrors.Create(404, "not_found", "That endpoint doesn't exist."), statusCode: 404)).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

public partial class Program
{
    /// <summary>The largest request body accepted by any endpoint except statement uploads.</summary>
    public const long MaxJsonRequestBytes = 1024 * 1024;
}
