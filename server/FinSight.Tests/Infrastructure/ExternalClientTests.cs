using System.Net;
using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinSight.Tests.Infrastructure;

public sealed class GeminiClientTests
{
    private static string Candidate(string text, string finish = "STOP") =>
        JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } }, finishReason = finish } } });

    private static (GeminiClient Client, StubHttpHandler Http) Create(string? apiKey = "secret-key")
    {
        var http = new StubHttpHandler();
        var client = new GeminiClient(new HttpClient(http), new FixedGeminiKeyResolver(apiKey), Options.Create(new GeminiOptions { TimeoutSeconds = 10 }), NullLogger<GeminiClient>.Instance);
        return (client, http);
    }

    private static Task<RawMerchantCategorizationResponse> Call(GeminiClient client) =>
        client.GenerateJsonAsync<RawMerchantCategorizationResponse>("system", "prompt", GeminiSchemas.MerchantCategorization(), 0.1, CancellationToken.None);

    [Fact]
    public async Task Parses_structured_output_and_keeps_the_key_out_of_the_url()
    {
        var (client, http) = Create();
        http.Json(Candidate("""{"results":[{"ref":"M1","categoryId":"food.coffee","confidence":0.9}]}"""));

        var result = await Call(client);

        result.Results.Should().ContainSingle().Which.CategoryId.Should().Be("food.coffee");
        var request = http.Requests.Single();
        request.Uri.ToString().Should().NotContain("secret-key");
        request.Headers["x-goog-api-key"].Should().Be("secret-key");
        request.Body.Should().Contain("\"responseSchema\"").And.Contain("\"responseMimeType\":\"application/json\"");
    }

    [Fact]
    public async Task Fails_fast_without_calling_gemini_when_not_configured()
    {
        var (client, http) = Create(apiKey: null);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.NotConfigured);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Retries_a_server_error_once()
    {
        var (client, http) = Create();
        http.Status(HttpStatusCode.ServiceUnavailable).Json(Candidate("""{"results":[]}"""));

        var result = await Call(client);

        result.Results.Should().BeEmpty();
        http.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AiFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AiFailure.Unavailable)]
    public async Task Reports_persistent_upstream_errors(HttpStatusCode status, AiFailure expected)
    {
        var (client, http) = Create();
        http.Status(status).Status(status);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(expected);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var (client, http) = Create();
        http.Status(HttpStatusCode.BadRequest, """{"error":{"message":"echoes the prompt"}}""");

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.Unavailable);
        http.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"candidates":"not-an-array"}""")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":42}]}}]}""")]
    public async Task Malformed_envelopes_are_reported_as_invalid_responses(string body)
    {
        var (client, http) = Create();
        http.Json(body);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.InvalidResponse);
    }

    [Fact]
    public async Task Output_that_is_not_the_requested_json_is_an_invalid_response()
    {
        var (client, http) = Create();
        http.Json(Candidate("Sure! Here are your categories: coffee."));

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.InvalidResponse);
    }

    [Fact]
    public async Task Network_failures_retry_once_then_report_unavailable()
    {
        var (client, http) = Create();
        http.Throw(new HttpRequestException("reset")).Throw(new HttpRequestException("reset"));

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.Unavailable);
        http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Service_batches_merchants_and_validates_every_batch()
    {
        var (client, http) = Create();
        var requests = Enumerable.Range(1, 45)
            .Select(i => new MerchantCategorizationRequest($"M{i}", $"merchant{i}", $"Merchant {i}", $"MERCHANT {i}", MerchantDirection.Out, 10m, 1))
            .ToList();
        http.Json(Candidate("""{"results":[{"ref":"M1","categoryId":"food.coffee","confidence":0.9},{"ref":"M2","categoryId":"not.real","confidence":0.9}]}"""))
            .Json(Candidate("""{"results":[{"ref":"M41","categoryId":"shopping.general","confidence":0.8},{"ref":"M1","categoryId":"food.coffee","confidence":0.9}]}"""));
        var service = new GeminiService(client, new FixedGeminiKeyResolver("k"));

        var results = await service.CategorizeTransactionsAsync(requests, CancellationToken.None);

        http.Requests.Should().HaveCount(2);
        results.Select(r => r.MerchantKey).Should().Equal("merchant1", "merchant41");
    }
}

public sealed class GmailApiClientTests
{
    private static (GmailApiClient Client, StubHttpHandler Http) Create()
    {
        var http = new StubHttpHandler();
        return (new GmailApiClient(new HttpClient(http)), http);
    }

    private const string Message = """
        {
          "id": "m1", "threadId": "t1", "snippet": "Your statement &amp; summary", "internalDate": "1788220800000",
          "payload": {
            "headers": [{ "name": "Subject", "value": "Your August eStatement" }, { "name": "From", "value": "Maple Bank <statements@maple.example>" }],
            "partId": "", "mimeType": "multipart/mixed",
            "parts": [
              { "partId": "0", "mimeType": "text/html", "filename": "", "body": { "size": 120 } },
              { "partId": "1", "mimeType": "application/pdf", "filename": "August.pdf", "body": { "size": 5, "attachmentId": "volatile-id" } }
            ]
          }
        }
        """;

    [Fact]
    public async Task Reads_headers_snippet_and_attachments_without_bodies()
    {
        var (client, http) = Create();
        http.Json(Message);

        var email = await client.GetMessageAsync("token", "m1", CancellationToken.None);

        email.Subject.Should().Be("Your August eStatement");
        email.From.Should().Contain("maple.example");
        email.Snippet.Should().Be("Your statement & summary");
        email.ReceivedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1788220800000));
        email.Attachments.Should().ContainSingle().Which.Should().Be(new FinSight.Core.Statements.EmailAttachment("1", "August.pdf", "application/pdf", 5));
        http.Requests.Single().Headers["Authorization"].Should().Be("Bearer token");
        Uri.UnescapeDataString(http.Requests.Single().Uri.Query).Should().Contain("fields=");
    }

    [Fact]
    public async Task Downloads_by_part_id_resolving_a_fresh_attachment_id()
    {
        var (client, http) = Create();
        http.Json(Message).Json("""{ "data": "JVBERi0-" }""");

        var bytes = await client.DownloadAttachmentAsync("token", "m1", "1", CancellationToken.None);

        bytes.Should().Equal("%PDF->"u8.ToArray());
        http.Requests[1].Uri.AbsolutePath.Should().EndWith("/messages/m1/attachments/volatile-id");
    }

    [Fact]
    public async Task A_missing_part_is_reported_as_missing_file()
    {
        var (client, http) = Create();
        http.Json(Message);

        var act = () => client.DownloadAttachmentAsync("token", "m1", "7", CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Unauthorized_means_the_grant_expired()
    {
        var (client, http) = Create();
        http.Status(HttpStatusCode.Unauthorized);

        var act = () => client.GetMessageAsync("token", "m1", CancellationToken.None);

        await act.Should().ThrowAsync<GmailAuthExpiredException>();
    }

    [Fact]
    public async Task Deleted_emails_are_reported_as_missing_file()
    {
        var (client, http) = Create();
        http.Status(HttpStatusCode.NotFound);

        var act = () => client.GetMessageAsync("token", "gone", CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Search_follows_pages_up_to_the_limit()
    {
        var (client, http) = Create();
        http.Json("""{ "messages": [{ "id": "a" }, { "id": "b" }], "nextPageToken": "p2" }""")
            .Json("""{ "messages": [{ "id": "c" }, { "id": "d" }], "nextPageToken": "p3" }""");

        var ids = await client.SearchMessageIdsAsync("token", "has:attachment filename:pdf", 3, CancellationToken.None);

        ids.Should().Equal("a", "b", "c");
        http.Requests.Should().HaveCount(2);
        http.Requests[1].Uri.Query.Should().Contain("pageToken=p2");
    }

    [Fact]
    public async Task Rate_limited_requests_are_retried()
    {
        var (client, http) = Create();
        http.Status(HttpStatusCode.TooManyRequests).Json("""{ "messages": [{ "id": "a" }] }""");

        var ids = await client.SearchMessageIdsAsync("token", "q", 10, CancellationToken.None);

        ids.Should().Equal("a");
    }

    [Theory]
    [InlineData("SGVsbG8", "Hello")]
    [InlineData("SGk_Pz8-", "Hi???>")]
    public void Decodes_unpadded_base64url(string data, string expected)
    {
        GmailApiClient.DecodeBase64Url(data).Should().Equal(System.Text.Encoding.ASCII.GetBytes(expected));
    }
}

public sealed class GoogleTokenServiceTests
{
    private static async Task<(TestDb Db, Guid UserId)> ConnectedUserAsync(bool undecryptableToken = false)
    {
        var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.GmailConnections.Add(new GmailConnection
            {
                UserId = user.Id,
                GoogleEmail = "sam@example.com",
                EncryptedRefreshToken = undecryptableToken ? "not-a-real-ciphertext" : ((ITokenProtector)testDb.Protector).Protect("refresh-1"),
                Scopes = GoogleIntegrationOptions.GmailReadonlyScope,
                Status = GmailConnectionStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        return (testDb, user.Id);
    }

    private static GoogleTokenService Service(TestDb testDb, Guid userId, StubHttpHandler http, IMemoryCache? cache = null) =>
        new(new HttpClient(http), testDb.Context(userId).Db, testDb.Protector, cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GoogleIntegrationOptions { ClientId = "client", ClientSecret = "secret" }), NullLogger<GoogleTokenService>.Instance);

    [Fact]
    public async Task Exchanges_the_stored_refresh_token_and_caches_the_access_token()
    {
        var (testDb, userId) = await ConnectedUserAsync();
        await using var _ = testDb;
        var http = new StubHttpHandler().Json("""{ "access_token": "access-1", "expires_in": 3599 }""");
        var service = Service(testDb, userId, http);

        (await service.GetAccessTokenAsync(userId, CancellationToken.None)).Should().Be("access-1");
        (await service.GetAccessTokenAsync(userId, CancellationToken.None)).Should().Be("access-1");

        http.Requests.Should().ContainSingle().Which.Body.Should().Contain("refresh_token=refresh-1").And.Contain("grant_type=refresh_token");
    }

    [Fact]
    public async Task A_revoked_grant_marks_the_connection_expired()
    {
        var (testDb, userId) = await ConnectedUserAsync();
        await using var _ = testDb;
        var service = Service(testDb, userId, new StubHttpHandler().Json("""{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest));

        var act = () => service.GetAccessTokenAsync(userId, CancellationToken.None);

        await act.Should().ThrowAsync<GmailAuthExpiredException>();
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            (await db.GmailConnections.SingleAsync()).Status.Should().Be(GmailConnectionStatus.Expired);
        }

        // Once expired, Google is not asked again until the user reconnects.
        var noCalls = new StubHttpHandler();
        await ((Func<Task>)(() => Service(testDb, userId, noCalls).GetAccessTokenAsync(userId, CancellationToken.None))).Should().ThrowAsync<GmailAuthExpiredException>();
        noCalls.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Credentials_that_can_no_longer_be_decrypted_require_reconnecting()
    {
        var (testDb, userId) = await ConnectedUserAsync(undecryptableToken: true);
        await using var _ = testDb;
        var http = new StubHttpHandler();

        var act = () => Service(testDb, userId, http).GetAccessTokenAsync(userId, CancellationToken.None);

        await act.Should().ThrowAsync<GmailAuthExpiredException>();
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_connection_gmail_is_not_connected()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();

        var act = () => Service(testDb, user.Id, new StubHttpHandler()).GetAccessTokenAsync(user.Id, CancellationToken.None);

        await act.Should().ThrowAsync<GmailNotConnectedException>();
    }

    [Fact]
    public async Task Disconnect_revokes_at_google_and_deletes_the_grant()
    {
        var (testDb, userId) = await ConnectedUserAsync();
        await using var _ = testDb;
        var http = new StubHttpHandler().Status(HttpStatusCode.OK);

        await Service(testDb, userId, http).DisconnectAsync(userId, CancellationToken.None);

        http.Requests.Single().Uri.ToString().Should().Contain("revoke");
        http.Requests.Single().Body.Should().Be("token=refresh-1");
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            (await db.GmailConnections.AnyAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Disconnect_still_deletes_the_grant_when_google_is_unreachable()
    {
        var (testDb, userId) = await ConnectedUserAsync();
        await using var _ = testDb;
        var http = new StubHttpHandler().Throw(new HttpRequestException("offline"));

        await Service(testDb, userId, http).DisconnectAsync(userId, CancellationToken.None);

        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            (await db.GmailConnections.AnyAsync()).Should().BeFalse();
        }
    }
}
