using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinSight.Core.Abstractions;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// Runs the real API against a throwaway SQLite database (or PostgreSQL, see <see cref="PostgresTestDatabase"/>), with demo mode on
/// and no external credentials.
/// </summary>
public class FinSightApiFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "finsight-tests", Guid.NewGuid().ToString("N"));
    private PostgresTestDatabase? _postgres;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string DatabasePath => Path.Combine(_directory, "test.db");

    /// <summary>Demo sign-ins allowed per hour. High by default so tests that create many demo users never throttle.</summary>
    protected virtual int DemoPerHour => 10_000;

    protected virtual string HostEnvironment => "Development";

    /// <summary>
    /// Empty by default. These settings take precedence over the developer's user secrets, so a real client id or secret on the
    /// machine never reaches a test host (GoogleAuthApiTests checks this).
    /// </summary>
    protected virtual string GoogleClientId => "";

    protected virtual string GoogleClientSecret => "";

    /// <summary>The server-level Gemini key. Empty by default, so AI is available only with a user's own key.</summary>
    protected virtual string GeminiApiKey => "";

    /// <summary>PostgreSQL instead of SQLite. Off unless FINSIGHT_TEST_PROVIDER=Postgres and FINSIGHT_TEST_POSTGRES are set.</summary>
    protected virtual bool UsePostgres => PostgresTestDatabase.IsDefaultProvider;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment(HostEnvironment);
        if (UsePostgres)
        {
            _postgres ??= PostgresTestDatabase.Create();
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:FinSight", _postgres.ConnectionString);
        }
        else
        {
            builder.UseSetting("ConnectionStrings:FinSight", $"Data Source={DatabasePath}");
        }

        builder.UseSetting("DataProtection:KeysPath", Path.Combine(_directory, "keys"));

        // Test key rings are temporary. Without this, a Production host on Linux (CI) refuses to start with an unencrypted key ring.
        builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
        builder.UseSetting("Demo:Enabled", "true");

        // The scheduler is driven directly by tests (AutomationApiTests), never by its timer; email goes to a temporary folder.
        builder.UseSetting("Automation:Enabled", "false");
        builder.UseSetting("Email:PickupDirectory", Path.Combine(_directory, "mail"));

        // Pending AI categorization retries are driven directly by tests (PendingCategorizationApiTests), never in the background.
        builder.UseSetting("Ai:PendingRetryEnabled", "false");
        builder.UseSetting("Gemini:RetryDelaySeconds", "0");
        builder.UseSetting("Google:ClientId", GoogleClientId);
        builder.UseSetting("Google:ClientSecret", GoogleClientSecret);
        builder.UseSetting("Gemini:ApiKey", GeminiApiKey);
        builder.UseSetting("RateLimits:DemoPerHour", DemoPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(ConfigureTestServices);
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>A client with its own cookie jar and the CSRF header every state-changing request needs.</summary>
    public HttpClient CreateSessionClient(bool withCsrfHeader = true, Uri? baseAddress = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
            BaseAddress = baseAddress ?? new Uri("http://localhost"),
        });
        if (withCsrfHeader)
        {
            client.DefaultRequestHeaders.Add("X-FinSight-Request", "1");
        }

        return client;
    }

    public async Task<HttpClient> CreateDemoClientAsync()
    {
        var client = CreateSessionClient();
        var response = await client.PostAsync("/api/auth/demo", null);
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _postgres?.Dispose();
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp files.
        }
    }
}

/// <summary>The API with a scripted Gemini in place of the real one.</summary>
public sealed class AiApiFactory : FinSightApiFactory
{
    internal FakeGeminiService Gemini { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IGeminiService>();
        services.AddSingleton<IGeminiService>(Gemini);
    }
}

/// <summary>
/// The API with dummy Google OAuth credentials, so the Google handler is registered. Google's token and userinfo endpoints are
/// replaced by <see cref="FakeGoogle"/>, which lets tests walk the whole redirect round trip without a network.
/// </summary>
public class GoogleApiFactory : FinSightApiFactory
{
    public const string ClientId = "1234567890-dummy.apps.googleusercontent.com";
    public const string ClientSecret = "dummy-client-secret";

    /// <summary>Where the browser reaches the app in development: the Vite dev server, which proxies /api.</summary>
    public static readonly Uri ViteOrigin = new("http://localhost:5173");

    internal FakeGoogle Google { get; } = new();

    protected override string GoogleClientId => ClientId;

    protected override string GoogleClientSecret => ClientSecret;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.Configure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = Google);

    public HttpClient CreateBrowserClient() => CreateSessionClient(baseAddress: ViteOrigin);

    /// <summary>A browser client signed in as a new Google user, through the real callback.</summary>
    public async Task<HttpClient> CreateGoogleUserClientAsync(string email = "sam@example.com")
    {
        var client = CreateBrowserClient();
        var challenge = await client.GetAsync("/api/auth/google");
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        var code = Google.Issue($"google-{Guid.NewGuid():N}", email);
        var callback = await client.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");
        callback.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        return client;
    }
}

/// <summary>
/// Google users who can save their own Gemini key. Google's key check (<see cref="KeyCheck"/>) and Gemini's generate endpoint
/// (<see cref="GeminiHttp"/>, which rejects every call by default) are scripted, and every log message is captured.
/// </summary>
public class AiKeyApiFactory : GoogleApiFactory
{
    internal StubHttpHandler KeyCheck { get; } = new();

    internal StubHttpHandler GeminiHttp { get; } = new() { Otherwise = _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) };

    internal CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        services.AddHttpClient<FinSight.Infrastructure.Gemini.GeminiKeyValidator>().ConfigurePrimaryHttpMessageHandler(() => KeyCheck);
        services.AddHttpClient<FinSight.Infrastructure.Gemini.GeminiClient>().ConfigurePrimaryHttpMessageHandler(() => GeminiHttp);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
    }
}

/// <summary>As <see cref="AiKeyApiFactory"/>, with a server-level Gemini key configured as well.</summary>
public sealed class ServerKeyApiFactory : AiKeyApiFactory
{
    public const string ServerKey = "server-gemini-key-0123456789";

    protected override string GeminiApiKey => ServerKey;
}

/// <summary>The API as it runs in production, for checks that development-only surface is absent.</summary>
public sealed class ProductionApiFactory : FinSightApiFactory
{
    protected override string HostEnvironment => "Production";
}

/// <summary>The API with a tiny demo sign-in allowance, to exercise rate limiting.</summary>
public sealed class ThrottledApiFactory : FinSightApiFactory
{
    protected override int DemoPerHour => 2;
}

internal static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(FinSightApiFactory.Json))!;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(FinSightApiFactory.Json);

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("code").GetString()!;

    public static async Task<JsonElement> UploadAsync(this HttpClient client, byte[] pdf, string filename = "statement.pdf", Guid? statementId = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", filename);
        if (statementId is not null)
        {
            content.Add(new StringContent(statementId.Value.ToString()), "statementId");
        }

        var response = await client.PostAsync("/api/uploads/statements", content);
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"Upload returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.JsonAsync();
    }

    /// <summary>Polls a background job until it finishes and returns its final state.</summary>
    public static async Task<JsonElement> WaitForJobAsync(this HttpClient client, string jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await (await client.GetAsync($"/api/jobs/{jobId}")).JsonAsync();
            if (job.GetProperty("status").GetString() is "succeeded" or "failed")
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The job did not finish.");
    }

    /// <summary>Uploads a PDF and waits for its processing job to succeed.</summary>
    public static async Task<Guid> ImportAsync(this HttpClient client, byte[] pdf, Guid? statementId = null)
    {
        var started = await client.UploadAsync(pdf, statementId: statementId);
        var job = await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!);
        job.GetProperty("status").GetString().Should().Be("succeeded", job.ToString());
        return started.GetProperty("statementId").GetGuid();
    }

    public static async Task<List<JsonElement>> TransactionsAsync(this HttpClient client, string query = "")
    {
        var page = await (await client.GetAsync($"/api/transactions?pageSize=200{(query.Length > 0 ? "&" + query : "")}")).JsonAsync();
        return page.GetProperty("items").EnumerateArray().ToList();
    }
}
