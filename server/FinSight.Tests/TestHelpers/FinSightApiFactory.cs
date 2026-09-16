using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FinSight.Tests.TestHelpers;

/// <summary>Runs the real API against a throwaway SQLite database, with demo mode on and no external credentials.</summary>
public sealed class FinSightApiFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "finsight-tests", Guid.NewGuid().ToString("N"));

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:FinSight", $"Data Source={Path.Combine(_directory, "test.db")}");
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(_directory, "keys"));
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Google:ClientId", "");
        builder.UseSetting("Google:ClientSecret", "");
        builder.UseSetting("Gemini:ApiKey", "");
    }

    /// <summary>A client with its own cookie jar and the CSRF header every state-changing request needs.</summary>
    public HttpClient CreateSessionClient(bool withCsrfHeader = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
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

internal static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(FinSightApiFactory.Json))!;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(FinSightApiFactory.Json);
}
