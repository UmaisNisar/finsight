using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>First-run onboarding state on the session, and the endpoint that finishes it.</summary>
public sealed class OnboardingApiTests(AiKeyApiFactory factory) : IClassFixture<AiKeyApiFactory>
{
    [Fact]
    public async Task A_new_google_user_starts_onboarding_and_completing_it_is_idempotent()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        session.GetProperty("user").GetProperty("onboardingCompleted").GetBoolean().Should().BeFalse();
        var userId = session.GetProperty("user").GetProperty("id").GetGuid();

        (await client.PostAsync("/api/onboarding/complete", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var first = (await ApiTestData.UserAsync(factory, userId)).OnboardingCompletedAt;

        (await client.PostAsync("/api/onboarding/complete", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        first.Should().NotBeNull();
        (await ApiTestData.UserAsync(factory, userId)).OnboardingCompletedAt.Should().Be(first, "completing again keeps the first completion time");
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("user").GetProperty("onboardingCompleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Completing_onboarding_needs_a_session_and_the_csrf_header()
    {
        (await factory.CreateBrowserClient().PostAsync("/api/onboarding/complete", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var client = await factory.CreateGoogleUserClientAsync();
        client.DefaultRequestHeaders.Remove("X-FinSight-Request");
        var response = await client.PostAsync("/api/onboarding/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.ErrorCodeAsync()).Should().Be("csrf_rejected");
    }

    [Fact]
    public async Task Demo_users_are_created_with_onboarding_completed()
    {
        var client = factory.CreateSessionClient();
        var started = await (await client.PostAsync("/api/auth/demo", null)).JsonAsync();
        started.GetProperty("onboardingCompleted").GetBoolean().Should().BeTrue();

        var user = (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("user");

        user.GetProperty("onboardingCompleted").GetBoolean().Should().BeTrue();
        (await ApiTestData.UserAsync(factory, user.GetProperty("id").GetGuid())).OnboardingCompletedAt.Should().NotBeNull();
    }
}

/// <summary>Saving, reading and removing a user's own Gemini key. Google's key check is scripted per test.</summary>
public sealed class AiKeyApiTests(AiKeyApiFactory factory) : IClassFixture<AiKeyApiFactory>
{
    [Fact]
    public async Task Without_a_key_the_status_reports_nothing_saved_and_no_server_key()
    {
        var client = await factory.CreateGoogleUserClientAsync();

        var status = await (await client.GetAsync("/api/ai/key")).JsonAsync();

        status.GetProperty("hasUserKey").GetBoolean().Should().BeFalse();
        status.GetProperty("hint").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("serverKeyAvailable").GetBoolean().Should().BeFalse();
        status.GetProperty("model").GetString().Should().Be("gemini-2.5-flash");
    }

    [Fact]
    public async Task A_key_google_accepts_is_checked_with_google_stored_encrypted_and_never_returned()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var key = ApiTestData.NewKey();
        factory.KeyCheck.Status(HttpStatusCode.OK, """{"models":[]}""");
        var checks = factory.KeyCheck.Requests.Count;

        var response = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = $"  {key}\n" });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain(key);
        var saved = JsonDocument.Parse(body).RootElement;
        saved.GetProperty("hasUserKey").GetBoolean().Should().BeTrue();
        saved.GetProperty("hint").GetString().Should().Be("…" + key[^4..]);

        var check = factory.KeyCheck.Requests.Skip(checks).Should().ContainSingle().Subject;
        check.Method.Should().Be(HttpMethod.Get);
        check.Uri.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
        check.Headers["x-goog-api-key"].Should().Be(key, "the key is sent trimmed, in a header rather than the URL");

        var user = await ApiTestData.UserAsync(factory, userId);
        user.EncryptedGeminiApiKey.Should().NotBeNullOrEmpty().And.NotContain(key);
        user.GeminiApiKeyHint.Should().Be(key[^4..]);
        factory.Services.GetRequiredService<IApiKeyProtector>().TryUnprotect(user.EncryptedGeminiApiKey!).Should().Be(key);
        factory.Services.GetRequiredService<ITokenProtector>().TryUnprotect(user.EncryptedGeminiApiKey!).Should().BeNull("keys use their own protection purpose");

        var status = await client.GetAsync("/api/ai/key");
        (await status.Content.ReadAsStringAsync()).Should().NotContain(key);
        factory.Logs.Messages.Should().NotContain(m => m.Contains(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("      ")]
    [InlineData("AIzaSy-too-short-12")]
    [InlineData("AIzaSy0123456789 abcdefghijklmnop")]
    public async Task Keys_that_cannot_be_valid_are_rejected_without_asking_google(string apiKey)
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var checks = factory.KeyCheck.Requests.Count;

        var response = await client.PutAsJsonAsync("/api/ai/key", new { apiKey });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorCodeAsync()).Should().Be("invalid_api_key");
        factory.KeyCheck.Requests.Should().HaveCount(checks);
    }

    [Fact]
    public async Task Overlong_and_missing_keys_are_rejected()
    {
        var client = await factory.CreateGoogleUserClientAsync();

        var overlong = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = new string('a', 201) });
        var missing = await client.PutAsJsonAsync("/api/ai/key", new { });

        (await overlong.ErrorCodeAsync()).Should().Be("invalid_api_key");
        (await missing.ErrorCodeAsync()).Should().Be("invalid_api_key");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400, "invalid_api_key")]
    [InlineData(HttpStatusCode.Unauthorized, 400, "invalid_api_key")]
    [InlineData(HttpStatusCode.Forbidden, 400, "invalid_api_key")]
    [InlineData(HttpStatusCode.TooManyRequests, 429, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, 503, "ai_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503, "ai_unavailable")]
    public async Task Google_answers_other_than_ok_save_nothing(HttpStatusCode google, int expectedStatus, string expectedCode)
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var key = ApiTestData.NewKey();
        factory.KeyCheck.Status(google, """{"error":{"message":"API key not valid."}}""");

        var response = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = key });
        var body = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(expectedStatus);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        body.Should().NotContain(key);
        if (expectedCode == "invalid_api_key")
        {
            JsonDocument.Parse(body).RootElement.GetProperty("message").GetString().Should().Be("Google didn't accept that key. Check you copied the whole key.");
        }

        (await (await client.GetAsync("/api/ai/key")).JsonAsync()).GetProperty("hasUserKey").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Network_failures_and_timeouts_report_ai_unavailable()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var key = ApiTestData.NewKey();
        factory.KeyCheck.Throw(new HttpRequestException("connection reset")).Throw(new TaskCanceledException("timed out"));

        var reset = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = key });
        var timeout = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = key });

        reset.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await reset.ErrorCodeAsync()).Should().Be("ai_unavailable");
        timeout.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await timeout.ErrorCodeAsync()).Should().Be("ai_unavailable");
        (await (await client.GetAsync("/api/ai/key")).JsonAsync()).GetProperty("hasUserKey").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_the_key_removes_it()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        await ApiTestData.SaveKeyAsync(factory, client, ApiTestData.NewKey());

        var response = await client.DeleteAsync("/api/ai/key");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var status = await (await client.GetAsync("/api/ai/key")).JsonAsync();
        status.GetProperty("hasUserKey").GetBoolean().Should().BeFalse();
        status.GetProperty("hint").ValueKind.Should().Be(JsonValueKind.Null);
        var user = await ApiTestData.UserAsync(factory, userId);
        user.EncryptedGeminiApiKey.Should().BeNull();
        user.GeminiApiKeyHint.Should().BeNull();
    }

    [Fact]
    public async Task Demo_users_can_read_the_status_but_not_change_the_key()
    {
        var client = await factory.CreateDemoClientAsync();
        var checks = factory.KeyCheck.Requests.Count;

        (await client.GetAsync("/api/ai/key")).StatusCode.Should().Be(HttpStatusCode.OK);
        var put = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = ApiTestData.NewKey() });
        var delete = await client.DeleteAsync("/api/ai/key");

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await put.ErrorCodeAsync()).Should().Be("demo_mode");
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await delete.ErrorCodeAsync()).Should().Be("demo_mode");
        factory.KeyCheck.Requests.Should().HaveCount(checks);
    }

    [Fact]
    public async Task Changing_the_key_needs_the_csrf_header()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        client.DefaultRequestHeaders.Remove("X-FinSight-Request");

        var put = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = ApiTestData.NewKey() });
        var delete = await client.DeleteAsync("/api/ai/key");

        (await put.ErrorCodeAsync()).Should().Be("csrf_rejected");
        (await delete.ErrorCodeAsync()).Should().Be("csrf_rejected");
    }

    [Fact]
    public async Task Saving_a_key_is_rate_limited_per_user()
    {
        var client = await factory.CreateGoogleUserClientAsync();
        for (var i = 0; i < 10; i++)
        {
            (await client.PutAsJsonAsync("/api/ai/key", new { apiKey = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var limited = await client.PutAsJsonAsync("/api/ai/key", new { apiKey = "short" });
        var otherUser = await (await factory.CreateGoogleUserClientAsync()).PutAsJsonAsync("/api/ai/key", new { apiKey = "short" });

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await limited.ErrorCodeAsync()).Should().Be("rate_limited");
        otherUser.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>Which key AI calls use: in requests, in background jobs, and in the session's capabilities.</summary>
public sealed class AiKeyResolutionApiTests(AiKeyApiFactory factory) : IClassFixture<AiKeyApiFactory>
{
    [Fact]
    public async Task Ai_capability_follows_each_users_own_key_when_the_server_has_none()
    {
        var withKey = await factory.CreateGoogleUserClientAsync("a@example.com");
        var withoutKey = await factory.CreateGoogleUserClientAsync("b@example.com");

        (await AiCapabilityAsync(withKey)).Should().BeFalse();
        await ApiTestData.SaveKeyAsync(factory, withKey, ApiTestData.NewKey());

        (await AiCapabilityAsync(withKey)).Should().BeTrue();
        (await AiConfiguredForAnalysisAsync(withKey)).Should().BeTrue();
        (await AiCapabilityAsync(withoutKey)).Should().BeFalse();
        (await AiConfiguredForAnalysisAsync(withoutKey)).Should().BeFalse();
        (await AiCapabilityAsync(factory.CreateBrowserClient())).Should().BeFalse("signed out, only the server key counts");

        (await withKey.DeleteAsync("/api/ai/key")).EnsureSuccessStatusCode();
        (await AiCapabilityAsync(withKey)).Should().BeFalse();
    }

    [Fact]
    public async Task A_background_job_uses_its_own_users_key_and_a_user_without_one_gets_no_ai()
    {
        var alice = await factory.CreateGoogleUserClientAsync("alice@example.com");
        var bob = await factory.CreateGoogleUserClientAsync("bob@example.com");
        var aliceKey = ApiTestData.NewKey();
        await ApiTestData.SaveKeyAsync(factory, alice, aliceKey);

        var aliceJob = await ApiTestData.ImportUnknownMerchantAsync(alice);
        var calls = factory.GeminiHttp.Requests.Count;
        var bobJob = await ApiTestData.ImportUnknownMerchantAsync(bob);

        factory.GeminiHttp.Requests.Should().Contain(r => r.Headers["x-goog-api-key"] == aliceKey);
        factory.GeminiHttp.Requests.Should().OnlyContain(r => r.Headers["x-goog-api-key"] == aliceKey, "only Alice has a key and there is no server key");
        ApiTestData.StepDetail(aliceJob, "categorize").Should().Be("Categorized with rules (AI temporarily unavailable)", "Gemini was called and (scripted to) fail");

        factory.GeminiHttp.Requests.Should().HaveCount(calls, "Bob's job must not call Gemini, least of all with Alice's key");
        ApiTestData.StepDetail(bobJob, "categorize").Should().Be("Categorized with rules (AI not configured)");
        factory.Logs.Messages.Should().NotContain(m => m.Contains(aliceKey));
    }

    private static async Task<bool> AiCapabilityAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("capabilities").GetProperty("ai").GetBoolean();

    private static async Task<bool> AiConfiguredForAnalysisAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("availability").GetProperty("configured").GetBoolean();
}

/// <summary>With a server key configured, a user's own key still wins, and users without one fall back to the server's.</summary>
public sealed class ServerKeyResolutionApiTests(ServerKeyApiFactory factory) : IClassFixture<ServerKeyApiFactory>
{
    [Fact]
    public async Task Jobs_prefer_the_users_key_and_fall_back_to_the_server_key()
    {
        var alice = await factory.CreateGoogleUserClientAsync("alice@example.com");
        var bob = await factory.CreateGoogleUserClientAsync("bob@example.com");
        var aliceKey = ApiTestData.NewKey();
        await ApiTestData.SaveKeyAsync(factory, alice, aliceKey);

        var calls = factory.GeminiHttp.Requests.Count;
        await ApiTestData.ImportUnknownMerchantAsync(alice);
        var aliceCalls = factory.GeminiHttp.Requests.Skip(calls).ToList();
        calls = factory.GeminiHttp.Requests.Count;
        await ApiTestData.ImportUnknownMerchantAsync(bob);
        var bobCalls = factory.GeminiHttp.Requests.Skip(calls).ToList();

        aliceCalls.Should().NotBeEmpty().And.OnlyContain(r => r.Headers["x-goog-api-key"] == aliceKey);
        bobCalls.Should().NotBeEmpty().And.OnlyContain(r => r.Headers["x-goog-api-key"] == ServerKeyApiFactory.ServerKey);
    }

    [Fact]
    public async Task Everyone_has_ai_and_the_status_reports_the_server_key()
    {
        var client = await factory.CreateGoogleUserClientAsync();

        var status = await (await client.GetAsync("/api/ai/key")).JsonAsync();
        var signedIn = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        var signedOut = await (await factory.CreateBrowserClient().GetAsync("/api/auth/session")).JsonAsync();

        status.GetProperty("hasUserKey").GetBoolean().Should().BeFalse();
        status.GetProperty("serverKeyAvailable").GetBoolean().Should().BeTrue();
        status.ToString().Should().NotContain(ServerKeyApiFactory.ServerKey);
        signedIn.GetProperty("capabilities").GetProperty("ai").GetBoolean().Should().BeTrue();
        signedOut.GetProperty("capabilities").GetProperty("ai").GetBoolean().Should().BeTrue();
    }
}

internal static class ApiTestData
{
    public static string NewKey() => $"AIzaSy{Guid.NewGuid():N}";

    public static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("user").GetProperty("id").GetGuid();

    public static async Task<User> UserAsync(FinSightApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        return await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Users.AsNoTracking().SingleAsync();
    }

    public static async Task SaveKeyAsync(AiKeyApiFactory factory, HttpClient client, string key)
    {
        factory.KeyCheck.Status(HttpStatusCode.OK, """{"models":[]}""");
        (await client.PutAsJsonAsync("/api/ai/key", new { apiKey = key })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Uploads a statement with a merchant no rule knows, so the job asks AI, and returns the finished job.</summary>
    public static async Task<JsonElement> ImportUnknownMerchantAsync(HttpClient client)
    {
        var started = await client.UploadAsync(PdfStatementBuilder.Chequing(2026, 1, 2000m, [(8, "ZXQ HOLDINGS REF 77", -45m)]));
        var job = await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!);
        job.GetProperty("status").GetString().Should().Be("succeeded", job.ToString());
        return job;
    }

    public static string? StepDetail(JsonElement job, string key) =>
        job.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("detail").GetString();
}

/// <summary>What the onboarding confirm step shows for each discovered statement.</summary>
public sealed class StatementDetectionApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task Statements_include_the_masked_subject_and_detection_reasons()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var now = DateTimeOffset.UtcNow;
        var statement = new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.Gmail,
            SourceKey = $"gmail:{Guid.NewGuid():N}:1",
            // Rows written before subjects were masked on discovery could hold a full account number.
            Subject = "Your eStatement for account 5012345678901234 is ready",
            Sender = "Maple Bank <statements@maple.example>",
            ReceivedAt = now,
            Filename = "eStatement.pdf",
            DocumentKind = DocumentKind.BankStatement,
            DetectionConfidence = 0.92,
            DetectionReasons = """["Sent by Maple Bank","Subject mentions a statement"]""",
            Institution = "Maple Bank",
            Status = StatementStatus.Discovered,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            db.Statements.Add(statement);
            await db.SaveChangesAsync();
        }

        var listBody = await (await client.GetAsync("/api/statements")).Content.ReadAsStringAsync();
        var detailBody = await (await client.GetAsync($"/api/statements/{statement.Id}")).Content.ReadAsStringAsync();

        listBody.Should().NotContain("5012345678901234");
        detailBody.Should().NotContain("5012345678901234");
        var item = JsonDocument.Parse(listBody).RootElement.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == statement.Id);
        item.GetProperty("subject").GetString().Should().Be("Your eStatement for account ••••1234 is ready");
        item.GetProperty("detectionReasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("Sent by Maple Bank", "Subject mentions a statement");
        item.GetProperty("senderName").GetString().Should().Be("Maple Bank");
        item.GetProperty("filename").GetString().Should().Be("eStatement.pdf");
        item.GetProperty("documentKind").GetString().Should().Be("bankStatement");
        item.GetProperty("detectionConfidence").GetDouble().Should().Be(0.92);
        JsonDocument.Parse(detailBody).RootElement.GetProperty("subject").GetString().Should().Be("Your eStatement for account ••••1234 is ready");

        var demoStatement = (await (await client.GetAsync("/api/statements")).JsonAsync()).EnumerateArray().First(s => s.GetProperty("id").GetGuid() != statement.Id);
        demoStatement.GetProperty("detectionReasons").ValueKind.Should().Be(JsonValueKind.Array, "reasons are always a list, never null");
    }
}
