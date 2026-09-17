using System.Net;
using System.Net.Http.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>AI features end to end, with Gemini replaced by a script. Tests in a class run one at a time, so each may set the script.</summary>
public sealed class InsightsApiTests : IClassFixture<AiApiFactory>
{
    private readonly AiApiFactory _factory;

    public InsightsApiTests(AiApiFactory factory)
    {
        _factory = factory;
        var gemini = factory.Gemini;
        gemini.IsConfigured = true;
        gemini.Categorize = _ => [];
        gemini.ReviewRecurring = _ => [];
        gemini.Analyze = facts => new GeminiAnalysisResult(AnalysisValidator.Validate(new RawAnalysis
        {
            Summary = $"You spent {facts.Expenses:C} and earned $123,456.",
            KeyInsights = [new RawInsight { Title = "Rent is your biggest cost", Description = "Housing takes the largest share.", Severity = "info" }],
        }, facts), "fake-model");
    }

    [Fact]
    public async Task Generated_analysis_is_validated_stored_and_goes_stale_when_data_changes()
    {
        var client = await _factory.CreateDemoClientAsync();

        var none = await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync();
        none.GetProperty("state").GetString().Should().Be("none");
        none.GetProperty("availability").GetProperty("configured").GetBoolean().Should().BeTrue();

        var generated = await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync();
        generated.GetProperty("state").GetString().Should().Be("fresh");
        generated.GetProperty("model").GetString().Should().Be("fake-model");
        generated.GetProperty("source").GetString().Should().Be("ai");
        generated.GetProperty("fallbackReason").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        generated.GetProperty("analysis").GetProperty("keyInsights").GetArrayLength().Should().Be(1);
        // The invented $123,456 matches nothing in the data and is flagged.
        generated.GetProperty("corrections").EnumerateArray().Should().Contain(c => c.GetProperty("message").GetString()!.Contains("123,456"));

        var facts = _factory.Gemini.AnalysisCalls[^1];
        facts.ToJson().Should().NotContain("PRE-AUTHORIZED").And.NotContain("4821").And.NotContain("demo@finsight.local");

        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("state").GetString().Should().Be("fresh");

        var rent = (await client.TransactionsAsync("categoryId=housing.rent&sort=date-desc"))[0];
        await client.PatchAsJsonAsync($"/api/transactions/{rent.GetProperty("id").GetString()}", new { isExcluded = true });

        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("state").GetString().Should().Be("stale");
    }

    [Fact]
    public async Task Analysis_respects_the_users_ai_setting()
    {
        var client = await _factory.CreateDemoClientAsync();
        var settings = await (await client.GetAsync("/api/settings")).JsonAsync();
        await client.PutAsJsonAsync("/api/settings", new
        {
            currency = settings.GetProperty("currency").GetString(),
            dateFormat = settings.GetProperty("dateFormat").GetString(),
            theme = "system",
            aiCategorizationEnabled = true,
            aiInsightsEnabled = false,
            notificationsEnabled = false,
        });
        var calls = _factory.Gemini.AnalysisCalls.Count;

        var response = await client.PostAsync("/api/analysis/generate?period=last-month", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("ai_disabled");
        _factory.Gemini.AnalysisCalls.Should().HaveCount(calls);
    }

    [Fact]
    public async Task Periods_without_data_are_not_sent_to_the_ai()
    {
        var client = await _factory.CreateDemoClientAsync();
        var calls = _factory.Gemini.AnalysisCalls.Count;

        var response = await client.PostAsync("/api/analysis/generate?period=custom&from=2001-01-01&to=2001-01-31", null);

        response.StatusCode.Should().Be((HttpStatusCode)422);
        (await response.ErrorCodeAsync()).Should().Be("not_enough_data");
        _factory.Gemini.AnalysisCalls.Should().HaveCount(calls);
    }

    [Theory]
    [InlineData(AiFailure.RateLimited, "quota_exhausted")]
    [InlineData(AiFailure.KeyRefused, "key_refused")]
    [InlineData(AiFailure.LimitReached, "limit_reached")]
    [InlineData(AiFailure.Timeout, "unavailable")]
    [InlineData(AiFailure.InvalidResponse, "unavailable")]
    [InlineData(AiFailure.Unavailable, "unavailable")]
    public async Task When_gemini_fails_finsight_writes_the_analysis_itself_and_says_why(AiFailure failure, string reason)
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.Analyze = _ => throw new AiUnavailableException(failure);

        var response = await client.PostAsync("/api/analysis/generate?period=last-month", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var generated = await response.JsonAsync();
        generated.GetProperty("state").GetString().Should().Be("fresh");
        generated.GetProperty("source").GetString().Should().Be("builtIn");
        generated.GetProperty("fallbackReason").GetString().Should().Be(reason);
        generated.GetProperty("model").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        generated.GetProperty("analysis").GetProperty("summary").GetString().Should().StartWith("In ");
        generated.GetProperty("corrections").GetArrayLength().Should().Be(0);

        var stored = await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync();
        stored.GetProperty("source").GetString().Should().Be("builtIn");
        stored.GetProperty("fallbackReason").GetString().Should().Be(reason);
        stored.GetProperty("model").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await client.GetAsync("/api/summary?period=last-month")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_a_key_the_analysis_is_written_without_calling_ai()
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.IsConfigured = false;
        var calls = _factory.Gemini.AnalysisCalls.Count;

        var generated = await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync();

        generated.GetProperty("source").GetString().Should().Be("builtIn");
        generated.GetProperty("fallbackReason").GetString().Should().Be("not_configured");
        generated.GetProperty("availability").GetProperty("configured").GetBoolean().Should().BeFalse();
        _factory.Gemini.AnalysisCalls.Should().HaveCount(calls);
    }

    [Fact]
    public async Task A_built_in_analysis_is_replaced_once_ai_answers_again()
    {
        var client = await _factory.CreateDemoClientAsync();
        var working = _factory.Gemini.Analyze;
        _factory.Gemini.Analyze = _ => throw new AiUnavailableException(AiFailure.RateLimited);
        (await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync()).GetProperty("source").GetString().Should().Be("builtIn");

        _factory.Gemini.Analyze = working;
        var generated = await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync();

        generated.GetProperty("source").GetString().Should().Be("ai");
        generated.GetProperty("model").GetString().Should().Be("fake-model");
        var stored = await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync();
        stored.GetProperty("source").GetString().Should().Be("ai");
        stored.GetProperty("fallbackReason").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_fresh_ai_analysis_is_kept_when_a_later_try_fails()
    {
        var client = await _factory.CreateDemoClientAsync();
        var first = await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync();
        _factory.Gemini.Analyze = _ => throw new AiUnavailableException(AiFailure.RateLimited);

        var second = await (await client.PostAsync("/api/analysis/generate?period=last-month", null)).JsonAsync();

        second.GetProperty("source").GetString().Should().Be("ai");
        second.GetProperty("analysis").GetProperty("summary").GetString().Should().Be(first.GetProperty("analysis").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Availability_says_when_a_new_try_with_ai_cannot_help()
    {
        var client = await _factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var health = _factory.Services.GetRequiredService<FinSight.Infrastructure.Gemini.AiKeyHealth>();

        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("availability").GetProperty("blocked").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);

        health.Record(userId, AiFailure.KeyRefused);
        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("availability").GetProperty("blocked").GetString()
            .Should().Be("key_refused");

        health.Clear(userId);
        (await (await client.GetAsync("/api/analysis?period=last-month")).JsonAsync()).GetProperty("availability").GetProperty("blocked").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Theory]
    [InlineData("period=next-year")]
    [InlineData("period=custom&from=2026-05-01")]
    [InlineData("period=custom&from=2020-01-01&to=2026-01-01")]
    public async Task Invalid_periods_are_rejected(string query)
    {
        var client = await _factory.CreateDemoClientAsync();

        var response = await client.GetAsync($"/api/summary?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorCodeAsync()).Should().Be("invalid_period");
    }

    [Fact]
    public async Task Recurring_payments_are_labelled_by_ai_when_it_answers()
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.ReviewRecurring = requests => requests
            .Where(r => r.Merchant == "Netflix")
            .Select(r => new RecurringReview(r.Ref, RecurringKind.Habit, "Streaming you might not watch"))
            .ToList();

        var recurring = await (await client.GetAsync("/api/recurring")).JsonAsync();

        recurring.GetProperty("aiReviewed").GetBoolean().Should().BeTrue();
        var netflix = recurring.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("merchant").GetString() == "Netflix");
        netflix.GetProperty("kind").GetString().Should().Be("habit");
        netflix.GetProperty("kindFromAi").GetBoolean().Should().BeTrue();
        recurring.GetProperty("annualTotal").GetDecimal().Should().Be(recurring.GetProperty("monthlyTotal").GetDecimal() * 12);
    }

    [Fact]
    public async Task Recurring_payments_are_still_detected_when_ai_is_unavailable()
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.ReviewRecurring = _ => throw new AiUnavailableException(AiFailure.Unavailable);

        var response = await client.GetAsync("/api/recurring");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var recurring = await response.JsonAsync();
        recurring.GetProperty("aiReviewed").GetBoolean().Should().BeFalse();
        recurring.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("merchant").GetString() == "Netflix" && i.GetProperty("kind").GetString() == "subscription"
            && !i.GetProperty("kindFromAi").GetBoolean());
    }

    [Fact]
    public async Task Ai_review_references_that_do_not_exist_are_ignored()
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.ReviewRecurring = _ => [new RecurringReview("R999", RecurringKind.Loan, "Invented"), new RecurringReview("Rx", RecurringKind.Loan, "Garbage")];

        var response = await client.GetAsync("/api/recurring");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("items").EnumerateArray().Should().NotContain(i => i.GetProperty("kind").GetString() == "loan");
    }

    [Fact]
    public async Task Uploads_send_only_unrecognised_merchants_to_ai_with_masked_descriptions()
    {
        var client = await _factory.CreateDemoClientAsync();
        _factory.Gemini.Categorize = requests => requests.Select(r => r.Direction == MerchantDirection.In
            ? new MerchantCategorization(r.MerchantKey, r.Direction, "income.freelance", TransactionType.Income, null, 0.9, "Client payment")
            : new MerchantCategorization(r.MerchantKey, r.Direction, "shopping.general", TransactionType.Expense, null, 0.85, "Online store")).ToList();
        var calls = _factory.Gemini.CategorizationCalls.Count;

        var statementId = await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 1, 2000m,
        [
            (6, "NETFLIX.COM", -20.99m),
            (8, "ZXQ HOLDINGS REF 4520123456789012", 950m),
            (14, "ZXQ HOLDINGS REF 4520123456789012", -45m),
        ]));

        var sent = _factory.Gemini.CategorizationCalls.Skip(calls).SelectMany(c => c).ToList();
        sent.Should().OnlyContain(r => r.MerchantKey.StartsWith("zxq", StringComparison.Ordinal));
        sent.Should().HaveCount(2);
        sent.Should().OnlyContain(r => !r.SampleDescription.Contains("4520123456789012"));

        var transactions = await client.TransactionsAsync($"statementId={statementId}");
        var payment = transactions.Single(t => t.GetProperty("amount").GetDecimal() == 950m);
        payment.GetProperty("categoryId").GetString().Should().Be("income.freelance");
        payment.GetProperty("type").GetString().Should().Be("income");
        payment.GetProperty("categorySource").GetString().Should().Be("ai");
        var charge = transactions.Single(t => t.GetProperty("amount").GetDecimal() == -45m);
        charge.GetProperty("categoryId").GetString().Should().Be("shopping.general");
        charge.GetProperty("type").GetString().Should().Be("expense");
    }
}
