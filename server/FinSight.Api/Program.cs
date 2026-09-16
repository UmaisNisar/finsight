using System.Text.Json;
using System.Text.Json.Serialization;
using FinSight.Api.Auth;
using FinSight.Api.Controllers;
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

builder.Services.AddFinSightInfrastructure(builder.Configuration);

var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? ".data/keys";
builder.Services.AddDataProtection()
    .SetApplicationName("FinSight")
    // On Windows the key ring is encrypted with DPAPI. On Linux, configure certificate protection for production.
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));

builder.Services
    .AddControllers()
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
builder.Services.AddFinSightRateLimiting();
builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var connectionString = app.Configuration.GetConnectionString("FinSight") ?? "Data Source=.data/finsight.db";
    if (connectionString.Contains(".data/", StringComparison.Ordinal))
    {
        Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, ".data"));
    }

    await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
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

public partial class Program;
