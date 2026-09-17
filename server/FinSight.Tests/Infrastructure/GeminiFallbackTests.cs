using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Gemini;
using FinSight.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinSight.Tests.Infrastructure;

/// <summary>The model fallback chain: which failures move on, which retry, which stop, and what is logged.</summary>
public sealed class GeminiChainTests
{
    private static readonly string[] Fallbacks = ["model-b", "model-c"];

    internal static string Candidate(string text) =>
        JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } }, finishReason = "STOP" } } });

    private static readonly string Answer = Candidate("""{"results":[{"ref":"M1","categoryId":"food.coffee","confidence":0.9}]}""");

    internal static GeminiService Service(GeminiClient client, IGeminiKeyResolver keys, AiOptions? ai = null, TimeProvider? time = null, AiKeyHealth? health = null) =>
        new(client, keys, new AiQuota(Options.Create(ai ?? new AiOptions()), time ?? TimeProvider.System), health ?? new AiKeyHealth(time ?? TimeProvider.System),
            NullLogger<GeminiService>.Instance);

    private static (GeminiClient Client, StubHttpHandler Http, CapturingLoggerProvider Logs) Create(
        string[]? fallbacks = null, int timeoutSeconds = 10, int attemptTimeoutSeconds = 10, HttpMessageHandler? handler = null)
    {
        var http = new StubHttpHandler();
        var logs = new CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var options = new GeminiOptions
        {
            Model = "model-a",
            FallbackModels = fallbacks ?? Fallbacks,
            TimeoutSeconds = timeoutSeconds,
            AttemptTimeoutSeconds = attemptTimeoutSeconds,
            RetryDelaySeconds = 0,
        };
        var client = new GeminiClient(new HttpClient(handler ?? http), new FixedGeminiKeyResolver("secret-key"), Options.Create(options), loggerFactory.CreateLogger<GeminiClient>());
        return (client, http, logs);
    }

    private static Task<GeminiResult<RawMerchantCategorizationResponse>> Call(GeminiClient client, int? thinkingBudget = null) =>
        client.GenerateAsync<RawMerchantCategorizationResponse>("system", "prompt", GeminiSchemas.MerchantCategorization(), 0.1, thinkingBudget, CancellationToken.None);

    private static string[] ModelsCalled(StubHttpHandler http) =>
        http.Requests.Select(r => r.Uri.AbsolutePath.Split('/')[^1].Split(':')[0]).ToArray();

    [Fact]
    public async Task A_used_up_primary_is_answered_by_the_next_model_without_a_retry()
    {
        var (client, http, logs) = Create();
        http.Status(HttpStatusCode.TooManyRequests, """{"error":{"status":"RESOURCE_EXHAUSTED"}}""").Json(Answer);

        var result = await Call(client);

        result.Model.Should().Be("model-b");
        result.Value.Results.Should().ContainSingle();
        ModelsCalled(http).Should().Equal("model-a", "model-b");
        logs.Messages.Should().Contain(m => m.Contains("fallback model model-b") && m.Contains("model-a"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid. Please pass a valid API key.","details":[{"reason":"API_KEY_INVALID"}]}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED","details":[{"reason":"SERVICE_DISABLED"}]}}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"API key expired. Please renew the API key."}}""")]
    public async Task A_refused_key_stops_the_chain_at_once(HttpStatusCode status, string body)
    {
        var (client, http, _) = Create();
        http.Status(status, body);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.KeyRefused);
        http.Requests.Should().ContainSingle("every model shares the key, so no other model is asked");
    }

    [Fact]
    public async Task A_server_error_is_retried_once_then_the_next_model_is_tried()
    {
        var (client, http, _) = Create();
        http.Status(HttpStatusCode.InternalServerError).Status(HttpStatusCode.ServiceUnavailable).Json(Answer);

        var result = await Call(client);

        result.Model.Should().Be("model-b");
        ModelsCalled(http).Should().Equal("model-a", "model-a", "model-b");
    }

    [Fact]
    public async Task Network_failures_are_retried_once_then_the_next_model_is_tried()
    {
        var (client, http, _) = Create();
        http.Throw(new HttpRequestException("reset")).Throw(new HttpRequestException("reset")).Json(Answer);

        (await Call(client)).Model.Should().Be("model-b");
        ModelsCalled(http).Should().Equal("model-a", "model-a", "model-b");
    }

    [Fact]
    public async Task Another_client_error_moves_to_the_next_model_without_a_retry()
    {
        var (client, http, logs) = Create();
        http.Status(HttpStatusCode.NotFound, """{"error":{"message":"models/model-a is not found. Your prompt was: SECRET PROMPT ECHO"}}""").Json(Answer);

        var result = await Call(client);

        result.Model.Should().Be("model-b");
        ModelsCalled(http).Should().Equal("model-a", "model-b");
        logs.Messages.Should().Contain(m => m.Contains("model-a") && m.Contains("404"));
        logs.Messages.Should().NotContain(m => m.Contains("SECRET PROMPT ECHO"), "response bodies can echo the prompt and are never logged");
        logs.Messages.Should().NotContain(m => m.Contains("secret-key"));
    }

    [Fact]
    public async Task Unusable_json_is_retried_once_then_the_next_model_is_tried()
    {
        var (client, http, _) = Create();
        http.Json(Candidate("Sure! Coffee.")).Json(Candidate("{\"results\": [")).Json(Answer);

        var result = await Call(client);

        result.Model.Should().Be("model-b");
        ModelsCalled(http).Should().Equal("model-a", "model-a", "model-b");
    }

    [Fact]
    public async Task Json_in_a_code_fence_is_accepted()
    {
        var (client, http, _) = Create();
        http.Json(Candidate("```json\n{\"results\":[{\"ref\":\"M1\",\"categoryId\":\"food.coffee\",\"confidence\":0.9}]}\n```"));

        (await Call(client)).Value.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_model_used_up_is_reported_as_rate_limited()
    {
        var (client, http, _) = Create();
        http.Status(HttpStatusCode.TooManyRequests).Status(HttpStatusCode.TooManyRequests).Status(HttpStatusCode.TooManyRequests);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.RateLimited);
        ModelsCalled(http).Should().Equal("model-a", "model-b", "model-c");
    }

    [Fact]
    public async Task A_used_up_quota_with_models_that_do_not_exist_is_still_rate_limited()
    {
        var (client, http, _) = Create();
        http.Status(HttpStatusCode.TooManyRequests).Status(HttpStatusCode.NotFound).Status(HttpStatusCode.NotFound);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.RateLimited);
    }

    [Fact]
    public async Task A_chain_that_ends_in_server_errors_is_unavailable()
    {
        var (client, http, _) = Create(fallbacks: ["model-b"]);
        http.Status(HttpStatusCode.TooManyRequests).Status(HttpStatusCode.BadGateway).Status(HttpStatusCode.BadGateway);

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.Unavailable);
        http.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_whole_chain_shares_one_time_budget()
    {
        var hanging = new HangingHandler();
        var (client, _, _) = Create(timeoutSeconds: 1, attemptTimeoutSeconds: 30, handler: hanging);
        var clock = Stopwatch.StartNew();

        var act = () => Call(client);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.Timeout);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        hanging.Calls.Should().Be(1, "the budget ran out during the first model, so no other model was started");
    }

    [Fact]
    public async Task A_model_that_hangs_is_skipped_for_the_next_within_the_budget()
    {
        var handler = new HangingHandler { AnswerFrom = "model-b", Answer = Answer };
        var (client, _, _) = Create(timeoutSeconds: 20, attemptTimeoutSeconds: 1, handler: handler);

        var result = await Call(client);

        result.Model.Should().Be("model-b");
        handler.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData("gemini-2.5-flash", 0, 0)]
    [InlineData("gemini-3.5-flash-lite", 0, 0)]
    [InlineData("gemini-2.5-flash", 2048, 2048)]
    [InlineData("gemini-2.5-pro", 0, null)]
    [InlineData("gemini-2.5-pro", 50, 128)]
    [InlineData("gemini-3.1-pro", 2048, 2048)]
    [InlineData("gemini-2.0-flash", 0, null)]
    [InlineData("gemini-1.5-pro", 2048, null)]
    public void Thinking_config_follows_the_model_family(string model, int budget, int? expected)
    {
        var config = GeminiClient.ThinkingConfig(model, budget);

        if (expected is null)
        {
            config.Should().BeNull();
        }
        else
        {
            config!["thinkingBudget"]!.GetValue<int>().Should().Be(expected.Value);
        }

        GeminiClient.ThinkingConfig(model, null).Should().BeNull("no budget asked for leaves the model's default");
    }

    [Fact]
    public async Task Each_model_in_the_chain_gets_its_own_thinking_config()
    {
        var (client, http, _) = Create(fallbacks: ["gemini-2.5-pro", "gemini-2.5-flash"]);
        http.Status(HttpStatusCode.TooManyRequests).Status(HttpStatusCode.TooManyRequests).Json(Answer);

        await Call(client, thinkingBudget: 0);

        http.Requests[0].Body.Should().NotContain("thinkingConfig", "model-a is not a thinking model");
        http.Requests[1].Body.Should().NotContain("thinkingConfig", "Pro can't turn thinking off, so it keeps its default");
        http.Requests[2].Body.Should().Contain("\"thinkingConfig\":{\"thinkingBudget\":0}");
        http.Requests.Should().OnlyContain(r => r.Body!.Contains("\"maxOutputTokens\":16384"));
    }

    [Fact]
    public void The_chain_is_the_primary_then_distinct_fallbacks()
    {
        new GeminiOptions { Model = "a", FallbackModels = [" b ", "a", "", "B", "c"] }.ModelChain().Should().Equal("a", "b", "c");
        new GeminiOptions { Model = "a", FallbackModels = [] }.ModelChain().Should().Equal("a");
        new GeminiOptions { Model = "gemini-2.5-flash" }.ModelChain().Should().Equal(["gemini-2.5-flash", .. GeminiOptions.DefaultFallbackModels.Where(m => m != "gemini-2.5-flash")]);
    }

    [Fact]
    public void Fallback_models_bind_from_configuration()
    {
        var configured = Bind(new() { ["Gemini:FallbackModels:0"] = "x", ["Gemini:FallbackModels:1"] = "y" });
        configured.ModelChain().Should().Equal("gemini-2.5-flash", "x", "y");

        Bind([]).ModelChain().Should().HaveCount(1 + GeminiOptions.DefaultFallbackModels.Count);
        Bind(new() { ["Gemini:FallbackModels"] = "" }).ModelChain().Should().Equal(["gemini-2.5-flash"], "an empty setting (Gemini__FallbackModels=) turns fallback off");

        static GeminiOptions Bind(Dictionary<string, string?> values)
        {
            var services = new ServiceCollection();
            services.AddOptions<GeminiOptions>().Bind(new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(GeminiOptions.Section));
            return services.BuildServiceProvider().GetRequiredService<IOptions<GeminiOptions>>().Value;
        }
    }

    /// <summary>Hangs until cancelled, except for requests to <see cref="AnswerFrom"/>.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        public string? AnswerFrom { get; init; }

        public string Answer { get; init; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (AnswerFrom is not null && request.RequestUri!.AbsolutePath.Contains($"/{AnswerFrom}:", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, System.Text.Encoding.UTF8, "application/json") };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}

/// <summary>FinSight's own daily allowances for Gemini calls.</summary>
public sealed class AiQuotaTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();

    private static (AiQuota Quota, MutableTimeProvider Clock) Create(int analysis = 20, int global = 400, params string[] priority)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 16, 23, 0, 0, TimeSpan.Zero));
        var options = new AiOptions { DailyLimits = new AiDailyLimits { Analysis = analysis, Categorization = 100 }, GlobalDailyLimit = global, PriorityEmails = priority };
        return (new AiQuota(Options.Create(options), clock), clock);
    }

    [Fact]
    public void Each_user_has_their_own_allowance_per_kind_of_call()
    {
        var (quota, _) = Create(analysis: 2);

        quota.TryConsume(Alice, "alice@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        quota.TryConsume(Alice, "alice@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);

        quota.TryConsume(Alice, "alice@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.UserLimit);
        quota.Peek(Alice, "alice@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.UserLimit);
        quota.TryConsume(Alice, "alice@example.com", serverKey: false, AiCallKind.Categorization).Should().Be(AiQuotaDecision.Allowed, "kinds are counted separately");
        quota.TryConsume(Bob, "bob@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed, "users are counted separately");
    }

    [Fact]
    public void The_server_key_has_a_shared_ceiling_that_priority_accounts_are_exempt_from()
    {
        var (quota, _) = Create(global: 2, priority: " OWNER@example.com ");

        quota.TryConsume(Alice, "alice@example.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        quota.TryConsume(Bob, "bob@example.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);

        quota.TryConsume(Alice, "alice@example.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.GlobalLimit);
        quota.TryConsume(Owner, "owner@EXAMPLE.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        quota.TryConsume(Bob, "bob@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed, "a user's own key isn't limited by the server key's ceiling");
    }

    [Fact]
    public void Calls_on_a_users_own_key_do_not_count_towards_the_shared_ceiling()
    {
        var (quota, _) = Create(analysis: 100, global: 1);

        for (var i = 0; i < 10; i++)
        {
            quota.TryConsume(Alice, "alice@example.com", serverKey: false, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        }

        quota.TryConsume(Bob, "bob@example.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        quota.TryConsume(Bob, "bob@example.com", serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.GlobalLimit);
    }

    [Fact]
    public void Allowances_reset_at_midnight_utc()
    {
        var (quota, clock) = Create(analysis: 1, global: 1);
        quota.TryConsume(Alice, null, serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
        quota.TryConsume(Alice, null, serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.UserLimit);
        quota.TryConsume(Bob, null, serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.GlobalLimit);

        clock.Now = clock.Now.AddMinutes(59);
        quota.TryConsume(Alice, null, serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.UserLimit, "still the same UTC day");

        clock.Now = clock.Now.AddMinutes(2);
        quota.TryConsume(Alice, null, serverKey: true, AiCallKind.Analysis).Should().Be(AiQuotaDecision.Allowed);
    }

    [Fact]
    public void Concurrent_calls_never_exceed_the_allowance()
    {
        var (quota, _) = Create(analysis: 50);

        var allowed = 0;
        Parallel.For(0, 500, _ =>
        {
            if (quota.TryConsume(Alice, null, serverKey: false, AiCallKind.Analysis) == AiQuotaDecision.Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        allowed.Should().Be(50);
    }

    [Fact]
    public async Task A_call_over_the_limit_never_reaches_google()
    {
        var http = new StubHttpHandler();
        var resolver = new DetailedKeyResolver(new GeminiKeyResolution("server-key", IsUserKey: false, Alice, "alice@example.com"));
        var client = new GeminiClient(new HttpClient(http), resolver, Options.Create(new GeminiOptions { FallbackModels = [] }), NullLogger<GeminiClient>.Instance);
        var service = GeminiChainTests.Service(client, resolver, new AiOptions { DailyLimits = new AiDailyLimits { RecurringReview = 1 } });
        http.Json(GeminiChainTests.Candidate("""{"results":[]}"""));
        var candidates = new[] { new RecurringReviewRequest("R1", "Netflix", "Subscriptions", 20m, "monthly", 6, false) };

        await service.DetectRecurringPatternsAsync(candidates, CancellationToken.None);
        var act = () => service.DetectRecurringPatternsAsync(candidates, CancellationToken.None);

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.LimitReached);
        http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_key_is_remembered_for_settings_until_a_call_succeeds()
    {
        var http = new StubHttpHandler();
        var resolver = new DetailedKeyResolver(new GeminiKeyResolution("user-key", IsUserKey: true, Alice, "alice@example.com"));
        var client = new GeminiClient(new HttpClient(http), resolver, Options.Create(new GeminiOptions { FallbackModels = [] }), NullLogger<GeminiClient>.Instance);
        var health = new AiKeyHealth(TimeProvider.System);
        var service = GeminiChainTests.Service(client, resolver, health: health);
        var candidates = new[] { new RecurringReviewRequest("R1", "Netflix", "Subscriptions", 20m, "monthly", 6, false) };
        http.Status(HttpStatusCode.BadRequest, "API_KEY_INVALID").Json(GeminiChainTests.Candidate("""{"results":[]}"""));

        var act = () => service.DetectRecurringPatternsAsync(candidates, CancellationToken.None);
        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Failure.Should().Be(AiFailure.KeyRefused);
        health.Get(Alice)!.Failure.Should().Be(AiFailure.KeyRefused);

        await service.DetectRecurringPatternsAsync(candidates, CancellationToken.None);
        health.Get(Alice).Should().BeNull();
    }

    private sealed class DetailedKeyResolver(GeminiKeyResolution resolution) : IGeminiKeyResolver
    {
        public Task<string?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(resolution.ApiKey);

        public Task<GeminiKeyResolution?> ResolveDetailsAsync(CancellationToken cancellationToken) => Task.FromResult<GeminiKeyResolution?>(resolution);
    }
}
