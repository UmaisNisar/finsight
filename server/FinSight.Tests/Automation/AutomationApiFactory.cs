using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Email;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinSight.Tests.Automation;

/// <summary>Records email instead of sending it. Can pretend the server has no email, or that delivery fails.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public bool IsConfigured { get; set; } = true;

    public bool Fail { get; set; }

    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public int Attempts;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Attempts);
        if (Fail)
        {
            throw new EmailDeliveryException("Scripted failure.");
        }

        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The API with Google users, an in-memory Gmail, a scripted Google token endpoint (refresh token "revoked" is rejected),
/// recorded email and a clock the test controls. The automation timer is off: tests run the scheduler directly.
/// </summary>
public sealed class AutomationApiFactory : GoogleApiFactory
{
    public static readonly DateTimeOffset Start = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    internal MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    internal FakeGmailClient Gmail { get; } = new();

    internal FakeEmailSender Email { get; } = new();

    internal CapturingLoggerProvider Logs { get; } = new();

    internal ConcurrentQueue<string> RefreshTokensUsed { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Automation:PauseBetweenUsers", "00:00:00");
        builder.UseSetting("Automation:JobPollInterval", "00:00:00.020");
        builder.UseSetting("RateLimits:TestEmailsPerHour", "3");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(Clock);
        services.RemoveAll<IGmailClient>();
        services.AddSingleton<IGmailClient>(Gmail);
        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(Email);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
        services.AddHttpClient<GoogleTokenService>().ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler
        {
            Otherwise = request =>
            {
                var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(form)["refresh_token"].ToString();
                RefreshTokensUsed.Enqueue(token);
                return token == "revoked"
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access","expires_in":3600}""", Encoding.UTF8, "application/json") };
            },
        });
    }

    internal async Task<User> AddUserAsync(string name, bool demo = false, bool autoScan = false, bool autoImport = false, bool digest = false,
        GmailConnectionStatus? gmail = null, string refreshToken = "refresh")
    {
        await using var scope = Services.CreateAsyncScope();
        using var system = scope.ServiceProvider.GetRequiredService<UserContext>().BeginSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
        var user = new User
        {
            Email = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
            DisplayName = name,
            IsDemo = demo,
            CreatedAt = Clock.GetUtcNow(),
            Settings = new UserSettings { AutoScanEnabled = autoScan, AutoImportEnabled = autoImport, MonthlyDigestEnabled = digest },
            AutoImportEnabledAt = autoImport ? Clock.GetUtcNow().AddHours(-1) : null,
        };
        db.Users.Add(user);
        if (gmail is { } status)
        {
            db.GmailConnections.Add(new GmailConnection
            {
                UserId = user.Id,
                GoogleEmail = user.Email,
                EncryptedRefreshToken = scope.ServiceProvider.GetRequiredService<ITokenProtector>().Protect(refreshToken),
                Scopes = GoogleIntegrationOptions.GmailReadonlyScope,
                Status = status,
                ConnectedAt = Clock.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Runs <paramref name="work"/> in a unit of work with ownership filters off, for arranging and inspecting data.</summary>
    internal async Task<T> SystemAsync<T>(Func<FinSightDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        using var system = scope.ServiceProvider.GetRequiredService<UserContext>().BeginSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<FinSightDbContext>());
    }

    internal async Task<T> AsUserAsync<T>(Guid userId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        return await work(scope.ServiceProvider);
    }
}
