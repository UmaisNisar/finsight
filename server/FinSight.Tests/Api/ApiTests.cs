using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinSight.Tests.TestHelpers;

namespace FinSight.Tests.Api;

public sealed class ApiTests : IClassFixture<FinSightApiFactory>
{
    private readonly FinSightApiFactory _factory;

    public ApiTests(FinSightApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_session_reports_capabilities_without_user()
    {
        var client = _factory.CreateSessionClient();

        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();

        session.GetProperty("authenticated").GetBoolean().Should().BeFalse();
        session.GetProperty("capabilities").GetProperty("demo").GetBoolean().Should().BeTrue();
        session.GetProperty("capabilities").GetProperty("ai").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/transactions")]
    [InlineData("/api/statements")]
    [InlineData("/api/summary")]
    [InlineData("/api/settings")]
    public async Task Financial_endpoints_require_authentication(string path)
    {
        var response = await _factory.CreateSessionClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task State_changing_requests_without_csrf_header_are_rejected()
    {
        var response = await _factory.CreateSessionClient(withCsrfHeader: false).PostAsync("/api/auth/demo", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("code").GetString().Should().Be("csrf_rejected");
    }

    [Fact]
    public async Task Api_responses_are_never_cached()
    {
        var response = await _factory.CreateSessionClient().GetAsync("/api/auth/session");

        response.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Demo_summary_is_computed_deterministically_with_transfers_excluded()
    {
        var client = await _factory.CreateDemoClientAsync();

        var body = await (await client.GetAsync("/api/summary?period=last-3-months")).JsonAsync();
        var summary = body.GetProperty("summary");

        var income = summary.GetProperty("income").GetDecimal();
        var expenses = summary.GetProperty("expenses").GetDecimal();
        income.Should().BeGreaterThan(15000);
        expenses.Should().BeGreaterThan(9000);
        summary.GetProperty("netCashFlow").GetDecimal().Should().Be(income - expenses);
        summary.GetProperty("savingsRate").GetDecimal().Should().Be(decimal.Round((income - expenses) / income * 100, 1, MidpointRounding.AwayFromZero));
        summary.GetProperty("transfers").GetProperty("count").GetInt32().Should().BeGreaterThan(0);

        // Card payments, savings transfers and investments must never appear as spending categories.
        var categoryIds = summary.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("categoryId").GetString()).ToList();
        categoryIds.Should().NotContain(["financial.credit-card-payments", "financial.transfers", "financial.investments"]);
        body.GetProperty("recurring").GetArrayLength().Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task One_user_can_never_see_or_edit_another_users_data()
    {
        var alice = await _factory.CreateDemoClientAsync();
        var bob = await _factory.CreateDemoClientAsync();

        var aliceStatements = await (await alice.GetAsync("/api/statements")).JsonAsync();
        var aliceStatementId = aliceStatements[0].GetProperty("id").GetString();
        var aliceTransactions = await (await alice.GetAsync("/api/transactions?pageSize=10")).JsonAsync();
        var aliceTransactionId = aliceTransactions.GetProperty("items")[0].GetProperty("id").GetString();

        (await bob.GetAsync($"/api/statements/{aliceStatementId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.PatchAsJsonAsync($"/api/transactions/{aliceTransactionId}", new { isExcluded = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.DeleteAsync($"/api/statements/{aliceStatementId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bobTransactions = await (await bob.GetAsync("/api/transactions?pageSize=200")).JsonAsync();
        bobTransactions.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().NotContain(aliceTransactionId);

        // Alice's data is untouched.
        (await alice.GetAsync($"/api/statements/{aliceStatementId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Editing_a_transaction_updates_the_summary()
    {
        var client = await _factory.CreateDemoClientAsync();
        var before = (await (await client.GetAsync("/api/summary?period=last-month")).JsonAsync()).GetProperty("summary").GetProperty("expenses").GetDecimal();

        // Demo data ends last month, so the most recent rent payment falls in the "last month" period.
        var rentPage = await (await client.GetAsync("/api/transactions?categoryId=housing.rent&sort=date-desc&pageSize=10")).JsonAsync();
        var rentId = rentPage.GetProperty("items")[0].GetProperty("id").GetString();
        var rentAmount = -rentPage.GetProperty("items")[0].GetProperty("amount").GetDecimal();
        rentAmount.Should().Be(1850m);

        var update = await client.PatchAsJsonAsync($"/api/transactions/{rentId}", new { isExcluded = true });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await (await client.GetAsync("/api/summary?period=last-month")).JsonAsync()).GetProperty("summary").GetProperty("expenses").GetDecimal();
        after.Should().Be(before - rentAmount);
    }

    [Fact]
    public async Task Uploaded_pdf_runs_through_the_pipeline_and_reimport_is_idempotent()
    {
        var client = await _factory.CreateDemoClientAsync();
        var pdf = PdfStatementBuilder.SampleChequingStatement();

        var first = await UploadAndWaitAsync(client, pdf);
        var statementId = first.GetProperty("statementId").GetString();

        var detail = await (await client.GetAsync($"/api/statements/{statementId}")).JsonAsync();
        var statement = detail.GetProperty("statement");
        statement.GetProperty("status").GetString().Should().Be("processed");
        statement.GetProperty("accountMask").GetString().Should().Be("7890");
        statement.GetProperty("periodEnd").GetString().Should().Be("2026-08-31");
        statement.GetProperty("extractionConfidence").GetDouble().Should().BeGreaterThan(0.9);

        var transactions = detail.GetProperty("transactions").EnumerateArray().ToList();
        transactions.Should().HaveCount(6);
        transactions.Select(t => t.GetProperty("amount").GetDecimal()).Should().Equal(3100.00m, -20.99m, -142.30m, -18.75m, -1850.00m, -400.00m);
        transactions.Single(t => t.GetProperty("merchant").GetString() == "Netflix").GetProperty("categoryId").GetString().Should().Be("entertainment.subscriptions");
        transactions.Single(t => t.GetProperty("amount").GetDecimal() == -400.00m).GetProperty("type").GetString().Should().Be("transfer");
        transactions.Should().OnlyContain(t => !t.GetProperty("description").GetString()!.Contains("4567890"));

        // Uploading the same file again reprocesses the same statement without duplicating anything.
        var second = await UploadAndWaitAsync(client, pdf);
        second.GetProperty("statementId").GetString().Should().Be(statementId);
        var again = await (await client.GetAsync($"/api/statements/{statementId}")).JsonAsync();
        again.GetProperty("transactions").GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task Non_pdf_uploads_are_rejected_with_a_readable_message()
    {
        var client = await _factory.CreateDemoClientAsync();
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("hello"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "statement.pdf");

        var response = await client.PostAsync("/api/uploads/statements", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("code").GetString().Should().Be("not_a_pdf");
    }

    [Fact]
    public async Task Ai_analysis_reports_unavailable_without_breaking_the_dashboard()
    {
        var client = await _factory.CreateDemoClientAsync();

        var response = await client.PostAsync("/api/analysis/generate?period=last-month", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var error = await response.JsonAsync();
        error.GetProperty("code").GetString().Should().Be("ai_not_configured");
        error.GetProperty("message").GetString().Should().Contain("transaction data is still available");
        error.TryGetProperty("stackTrace", out _).Should().BeFalse();

        (await client.GetAsync("/api/summary?period=last-month")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_all_data_leaves_an_empty_account()
    {
        var client = await _factory.CreateDemoClientAsync();

        (await client.DeleteAsync("/api/data")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await (await client.GetAsync("/api/statements")).JsonAsync()).GetArrayLength().Should().Be(0);
        (await (await client.GetAsync("/api/transactions")).JsonAsync()).GetProperty("total").GetInt32().Should().Be(0);
    }

    private static async Task<JsonElement> UploadAndWaitAsync(HttpClient client, byte[] pdf)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "august-statement.pdf");

        var response = await client.PostAsync("/api/uploads/statements", content);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var started = await response.JsonAsync();
        var jobId = started.GetProperty("jobId").GetString();

        for (var i = 0; i < 100; i++)
        {
            var job = await (await client.GetAsync($"/api/jobs/{jobId}")).JsonAsync();
            var status = job.GetProperty("status").GetString();
            if (status is "succeeded" or "failed")
            {
                status.Should().Be("succeeded", job.ToString());
                return started;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The processing job did not finish.");
    }
}
