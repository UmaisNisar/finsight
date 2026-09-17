using System.Text.Json;
using System.Text.Json.Serialization;
using FinSight.Api.Auth;
using FinSight.Api.Controllers;
using FinSight.Api.Hosting;
using FinSight.Api.Middleware;
using FinSight.Infrastructure;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Secrets (Google client secret, Gemini API key) come from user secrets in development and
// environment variables in production. appsettings.Local.json is git-ignored for convenience.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// A relative SQLite path resolves against the app's content root, not whatever directory it was started from,
// so the database and the Data Protection keys always live side by side.
var connectionString = SqlitePaths.Resolve(builder.Configuration.GetConnectionString("FinSight") ?? "Data Source=.data/finsight.db", builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:FinSight"] = connectionString;

builder.Services.AddFinSightInfrastructure(builder.Configuration);

var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? ".data/keys";
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("FinSight")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));

// The key ring decrypts every encrypted field, so it must not sit next to the database in plain form. With a custom key
// directory, Data Protection writes keys unencrypted unless told otherwise: use a certificate when one is configured
// (DataProtection:CertificatePath and DataProtection:CertificatePassword, a PKCS#12 file), else DPAPI on Windows.
// On other platforms without a certificate the keys stay unencrypted, and Data Protection logs a warning when it writes one.
var keyCertificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(keyCertificatePath))
{
    var keyCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
        Path.GetFullPath(keyCertificatePath, builder.Environment.ContentRootPath), builder.Configuration["DataProtection:CertificatePassword"]);
    dataProtection.ProtectKeysWithCertificate(keyCertificate).UnprotectKeysWithAnyCertificate(keyCertificate);
}
else if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

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

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Database.MigrateAsync();
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

app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.Json(ApiErrors.Create(404, "not_found", "That endpoint doesn't exist."), statusCode: 404)).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

await app.RunAsync();

public partial class Program
{
    /// <summary>The largest request body accepted by any endpoint except statement uploads.</summary>
    public const long MaxJsonRequestBytes = 1024 * 1024;
}
