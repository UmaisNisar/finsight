using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinSight.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>Regression tests for the security review: PDFs never touch disk, and uploads can't exhaust server memory.</summary>
public sealed class UploadHardeningApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task A_large_upload_is_buffered_in_memory_and_never_spills_to_a_temp_file()
    {
        // ASP.NET Core buffers multipart files above 64 KB to ASPNETCORE_*.tmp files in this directory unless told otherwise.
        var tempDirectory = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath();
        var created = new ConcurrentQueue<string>();
        using var watcher = new FileSystemWatcher(tempDirectory, "ASPNETCORE_*.tmp") { IncludeSubdirectories = false };
        watcher.Created += (_, e) => created.Enqueue(e.Name ?? string.Empty);
        watcher.EnableRaisingEvents = true;

        var client = await factory.CreateDemoClientAsync();
        var pdf = new byte[2 * 1024 * 1024];
        "%PDF-1.7\n"u8.CopyTo(pdf);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "large-statement.pdf");
        var response = await client.PostAsync("/api/uploads/statements", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Task.Delay(300);
        created.Should().BeEmpty("statement PDFs must be held in memory only");
    }

    [Fact]
    public async Task Another_users_statement_ids_are_treated_as_missing_everywhere_they_are_accepted()
    {
        var alice = await factory.CreateDemoClientAsync();
        var bob = await factory.CreateDemoClientAsync();
        var aliceStatementId = (await (await alice.GetAsync("/api/statements")).JsonAsync())[0].GetProperty("id").GetGuid();

        (await bob.PostAsync($"/api/statements/{aliceStatementId}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.PostAsJsonAsync("/api/statements/process", new { statementIds = new[] { aliceStatementId } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bob.TransactionsAsync($"statementId={aliceStatementId}")).Should().BeEmpty();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakePdf(1024));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "statement.pdf");
        content.Add(new StringContent(aliceStatementId.ToString()), "statementId");
        (await bob.PostAsync("/api/uploads/statements", content)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await alice.TransactionsAsync($"statementId={aliceStatementId}")).Should().NotBeEmpty();
    }

    internal static byte[] FakePdf(int size)
    {
        var pdf = new byte[size];
        "%PDF-1.7\n"u8.CopyTo(pdf);
        return pdf;
    }
}

/// <summary>The API with only 1 MB of memory for uploads waiting to be processed.</summary>
public sealed class SmallUploadBudgetApiFactory : FinSightApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Uploads:MaxQueuedMegabytes", "1");
    }
}

public sealed class UploadMemoryBudgetApiTests(SmallUploadBudgetApiFactory factory) : IClassFixture<SmallUploadBudgetApiFactory>
{
    [Fact]
    public async Task Uploads_beyond_the_servers_memory_budget_are_turned_away_and_the_budget_is_released_after_processing()
    {
        var client = await factory.CreateDemoClientAsync();

        var tooLarge = await PostAsync(client, UploadHardeningApiTests.FakePdf(2 * 1024 * 1024));
        tooLarge.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await tooLarge.ErrorCodeAsync()).Should().Be("rate_limited");

        // Two uploads that each fit, one after the other: the first job's memory is released when it finishes.
        for (var i = 0; i < 2; i++)
        {
            var pdf = UploadHardeningApiTests.FakePdf(700 * 1024);
            pdf[^1] = (byte)i;
            var started = await client.UploadAsync(pdf);
            (await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!)).GetProperty("status").GetString().Should().Be("succeeded");
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] pdf)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "statement.pdf");
        return await client.PostAsync("/api/uploads/statements", content);
    }
}

public sealed class SessionApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task Signing_out_ends_the_session_on_the_server_so_a_copied_cookie_stops_working()
    {
        var client = await factory.CreateDemoClientAsync();
        var copy = factory.CreateSessionClient();
        copy.DefaultRequestHeaders.Add("Cookie", (await client.GetAsync("/api/auth/session")).RequestMessage!.Headers.GetValues("Cookie").Single());
        (await copy.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await copy.GetAsync("/api/settings");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("session_ended");
    }

    [Fact]
    public async Task Signing_out_one_session_leaves_the_users_other_sessions_alone()
    {
        var first = await factory.CreateDemoClientAsync();
        var second = await factory.CreateDemoClientAsync();

        (await first.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await second.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>The API on a clock the test controls.</summary>
public sealed class ClockApiFactory : FinSightApiFactory
{
    internal MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureTestServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<TimeProvider>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TimeProvider>(services, Clock);
    }
}

public sealed class SessionLifetimeApiTests(ClockApiFactory factory) : IClassFixture<ClockApiFactory>
{
    [Fact]
    public async Task An_active_session_still_ends_thirty_days_after_sign_in()
    {
        var client = await factory.CreateDemoClientAsync();

        // Used every six days, the sliding cookie never expires on its own.
        for (var day = 6; day < 30; day += 6)
        {
            factory.Clock.Now += TimeSpan.FromDays(6);
            (await client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.OK, $"day {day} is within the session's lifetime");
        }

        factory.Clock.Now += TimeSpan.FromDays(6);
        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("session_ended");
    }
}

/// <summary>The API with its Data Protection key ring encrypted by a certificate, as a Linux deployment would configure it.</summary>
public sealed class CertificateKeyRingApiFactory : FinSightApiFactory
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "finsight-tests", "keyring-" + Guid.NewGuid().ToString("N"));

    public string KeysPath => Path.Combine(_directory, "keys");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Directory.CreateDirectory(_directory);

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=FinSight test key ring", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var certificatePath = Path.Combine(_directory, "keyring.pfx");
        File.WriteAllBytes(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-password"));

        builder.UseSetting("DataProtection:KeysPath", KeysPath);
        builder.UseSetting("DataProtection:CertificatePath", certificatePath);
        builder.UseSetting("DataProtection:CertificatePassword", "test-password");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp files.
        }
    }
}

public sealed class KeyRingApiTests(CertificateKeyRingApiFactory factory) : IClassFixture<CertificateKeyRingApiFactory>
{
    [Fact]
    public async Task The_key_ring_is_encrypted_with_the_configured_certificate_and_data_still_decrypts()
    {
        var client = await factory.CreateDemoClientAsync();

        var keys = Directory.GetFiles(factory.KeysPath, "key-*.xml").Select(File.ReadAllText).ToList();
        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(k => k.Contains("<EncryptedData", StringComparison.Ordinal) && !k.Contains("unencrypted form", StringComparison.Ordinal));

        (await client.TransactionsAsync()).Should().NotBeEmpty()
            .And.OnlyContain(t => t.GetProperty("description").GetString() != "[unavailable]");
    }
}

public sealed class WindowsKeyRingApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task Without_a_certificate_the_key_ring_is_encrypted_with_DPAPI_on_Windows()
    {
        // DPAPI only exists on Windows; elsewhere the certificate test above covers key ring encryption.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await factory.CreateDemoClientAsync();

        var keysPath = Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "keys");
        var keys = Directory.GetFiles(keysPath, "key-*.xml").Select(File.ReadAllText).ToList();
        keys.Should().NotBeEmpty().And.OnlyContain(k => k.Contains("DpapiXmlDecryptor", StringComparison.Ordinal));
    }
}

/// <summary>The API on a real Kestrel server: request body limits are enforced by the server, which TestServer does not do.</summary>
public sealed class KestrelApiFactory : FinSightApiFactory
{
    public KestrelApiFactory() => UseKestrel(0);

    public HttpClient CreateKestrelClient()
    {
        StartServer();
        var address = Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-FinSight-Request", "1");
        return client;
    }
}

public sealed class RequestLimitApiTests(KestrelApiFactory factory) : IClassFixture<KestrelApiFactory>
{
    [Fact]
    public async Task Oversized_json_bodies_are_refused_while_statement_uploads_keep_their_larger_limit()
    {
        using var client = factory.CreateKestrelClient();
        (await client.PostAsync("/api/auth/demo", null)).EnsureSuccessStatusCode();

        var body = "{\"statementIds\":[" + string.Join(',', Enumerable.Repeat("\"00000000-0000-0000-0000-000000000001\"", 40_000)) + "]}";
        body.Length.Should().BeGreaterThan((int)Program.MaxJsonRequestBytes);
        var json = await client.PostAsync("/api/statements/process", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        json.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await json.ErrorCodeAsync()).Should().Be("request_too_large");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(UploadHardeningApiTests.FakePdf(2 * 1024 * 1024));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "statement.pdf");
        (await client.PostAsync("/api/uploads/statements", content)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
