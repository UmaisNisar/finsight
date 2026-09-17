using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Persistence;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>Statement alerts: emails saying a statement is ready without attaching it, fulfilled by uploading the PDF.</summary>
public sealed class StatementAlertsApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    private static readonly (int, string, decimal)[] JuneRows = [(4, "NETFLIX.COM", -20.99m), (12, "LOBLAWS #221", -84.10m)];

    private async Task<Statement> AddAlertAsync(Guid userId, string institution, string? mask, DateTimeOffset received, AccountType type = AccountType.Chequing)
    {
        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");
        var alert = new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.Gmail,
            SourceKey = StatementAlert.SourceKey(messageId),
            SourceMessageId = messageId,
            Subject = "eStatement Alert",
            Sender = "CIBC Banking <mailbox.noreply@cibc.com>",
            ReceivedAt = received,
            Filename = string.Empty,
            DocumentKind = DocumentKind.BankStatement,
            DetectionConfidence = 0.9,
            DetectionReasons = """["Sent by CIBC","Subject says a statement is ready","No PDF attached"]""",
            Institution = institution,
            AccountType = type,
            AccountMask = mask,
            Status = StatementStatus.AwaitingUpload,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
        db.Statements.Add(alert);
        await db.SaveChangesAsync();
        return alert;
    }

    private async Task<Statement?> RowAsync(Guid userId, Guid statementId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        return await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Statements.AsNoTracking().SingleOrDefaultAsync(s => s.Id == statementId);
    }

    private static async Task<List<JsonElement>> StatementsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/statements")).JsonAsync()).EnumerateArray().ToList();

    [Fact]
    public async Task Alerts_are_listed_with_a_title_and_trusted_sign_in_guidance()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var received = new DateTimeOffset(2026, 9, 15, 13, 0, 0, TimeSpan.Zero);
        var alert = await AddAlertAsync(userId, "CIBC", "5190", received, AccountType.CreditCard);

        var item = (await StatementsAsync(client)).Single(s => s.GetProperty("id").GetGuid() == alert.Id);

        item.GetProperty("title").GetString().Should().Be("CIBC credit card ending 5190");
        item.GetProperty("status").GetString().Should().Be("awaitingUpload");
        item.GetProperty("receivedAt").GetDateTimeOffset().Should().Be(received);
        item.GetProperty("signInUrl").GetString().Should().Be("https://www.cibconline.cibc.com");
        item.GetProperty("downloadHint").GetString().Should().Be("Sign in to CIBC Online Banking, open My documents, and download each month's statement as a PDF.");
        item.GetProperty("reprocessNeedsUpload").GetBoolean().Should().BeTrue();
        item.GetProperty("source").GetString().Should().Be("gmail");
        item.GetProperty("filename").GetString().Should().BeEmpty();

        // Statements from banks FinSight can't name carry no link.
        var demo = (await StatementsAsync(client)).First(s => s.GetProperty("id").GetGuid() != alert.Id);
        demo.GetProperty("signInUrl").ValueKind.Should().Be(JsonValueKind.Null);
        demo.GetProperty("downloadHint").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Processing_rejects_alerts_because_they_need_an_upload()
    {
        var client = await factory.CreateDemoClientAsync();
        var alert = await AddAlertAsync(await ApiTestData.UserIdAsync(client), "CIBC", "5190", DateTimeOffset.UtcNow);

        var batch = await client.PostAsJsonAsync("/api/statements/process", new { statementIds = new[] { alert.Id } });
        var single = await client.PostAsync($"/api/statements/{alert.Id}/process", null);

        batch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await batch.ErrorCodeAsync()).Should().Be("upload_required");
        single.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await single.ErrorCodeAsync()).Should().Be("upload_required");
    }

    [Fact]
    public async Task Dismissing_hides_an_alert_and_only_works_on_alerts()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var alert = await AddAlertAsync(userId, "CIBC", "5190", DateTimeOffset.UtcNow);
        var processed = (await StatementsAsync(client)).First(s => s.GetProperty("status").GetString() == "processed").GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/statements/{alert.Id}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StatementsAsync(client)).Should().NotContain(s => s.GetProperty("id").GetGuid() == alert.Id);
        (await client.GetAsync($"/api/statements/{alert.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/statements/{alert.Id}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RowAsync(userId, alert.Id))!.Status.Should().Be(StatementStatus.Dismissed, "the row stays so a rescan skips the email");

        var notAlert = await client.PostAsync($"/api/statements/{processed}/dismiss", null);
        notAlert.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await notAlert.ErrorCodeAsync()).Should().Be("not_awaiting_upload");

        var other = await factory.CreateDemoClientAsync();
        (await other.PostAsync($"/api/statements/{(await AddAlertAsync(userId, "CIBC", "1111", DateTimeOffset.UtcNow)).Id}/dismiss", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_an_alert_keeps_a_dismissed_row_so_a_rescan_cannot_recreate_it()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var alert = await AddAlertAsync(userId, "CIBC", "5190", DateTimeOffset.UtcNow);
        await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 7, 900m, JuneRows, note: Guid.NewGuid().ToString()), alert.Id);

        (await client.DeleteAsync($"/api/statements/{alert.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StatementsAsync(client)).Should().NotContain(s => s.GetProperty("id").GetGuid() == alert.Id);
        (await client.TransactionsAsync($"statementId={alert.Id}")).Should().BeEmpty();
        var row = await RowAsync(userId, alert.Id);
        row.Should().NotBeNull();
        row!.Status.Should().Be(StatementStatus.Dismissed);
        row.ContentHash.Should().BeNull();
        (await client.DeleteAsync($"/api/statements/{alert.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uploading_with_the_alerts_id_processes_the_pdf_into_that_statement()
    {
        var client = await factory.CreateDemoClientAsync();
        var alert = await AddAlertAsync(await ApiTestData.UserIdAsync(client), "CIBC", "5190", new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));

        var statementId = await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 6, 900m, JuneRows, note: Guid.NewGuid().ToString()), alert.Id);

        statementId.Should().Be(alert.Id);
        var detail = await (await client.GetAsync($"/api/statements/{alert.Id}")).JsonAsync();
        var statement = detail.GetProperty("statement");
        statement.GetProperty("status").GetString().Should().Be("processed");
        statement.GetProperty("title").GetString().Should().Be("June 2026");
        statement.GetProperty("transactionCount").GetInt32().Should().Be(2);
        statement.GetProperty("institution").GetString().Should().Be("CIBC", "the PDF names no bank, so the alert's stays");
        detail.GetProperty("transactions").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task An_upload_clears_the_closest_alert_for_the_same_bank_and_account()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        DateTimeOffset On(int month, int day) => new(2026, month, day, 12, 0, 0, TimeSpan.Zero);

        // The June statement ends June 30 and is for CIBC chequing ••7890.
        var match = await AddAlertAsync(userId, "CIBC", "7890", On(7, 3));
        var laterNoMask = await AddAlertAsync(userId, "CIBC", null, On(7, 20));
        var otherAccount = await AddAlertAsync(userId, "CIBC", "1111", On(7, 1));
        var outOfWindow = await AddAlertAsync(userId, "CIBC", "7890", On(8, 9));
        var tooEarly = await AddAlertAsync(userId, "CIBC", "7890", On(6, 24));
        var otherBank = await AddAlertAsync(userId, "TD Bank", "7890", On(6, 30));

        var started = await client.UploadAsync(PdfStatementBuilder.Chequing(2026, 6, 900m, JuneRows, note: Guid.NewGuid().ToString(), institution: "CIBC"));
        var job = await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!);

        job.GetProperty("status").GetString().Should().Be("succeeded");
        job.GetProperty("steps")[0].GetProperty("detail").GetString().Should().Be("2 transactions · statement alert cleared");
        var uploaded = await RowAsync(userId, started.GetProperty("statementId").GetGuid());
        uploaded!.Institution.Should().Be("CIBC");
        uploaded.AccountMask.Should().Be("7890");

        (await RowAsync(userId, match.Id))!.Status.Should().Be(StatementStatus.Dismissed);
        var awaiting = (await StatementsAsync(client))
            .Where(s => s.GetProperty("status").GetString() == "awaitingUpload")
            .Select(s => s.GetProperty("id").GetGuid());
        awaiting.Should().BeEquivalentTo([laterNoMask.Id, otherAccount.Id, outOfWindow.Id, tooEarly.Id, otherBank.Id]);
    }

    [Fact]
    public async Task Deleting_all_transactions_returns_fulfilled_alerts_to_awaiting_upload_and_keeps_dismissed_ones_hidden()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var fulfilled = await AddAlertAsync(userId, "CIBC", "5190", new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var dismissed = await AddAlertAsync(userId, "CIBC", "1111", DateTimeOffset.UtcNow);
        await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 4, 900m, JuneRows, note: Guid.NewGuid().ToString()), fulfilled.Id);
        (await client.PostAsync($"/api/statements/{dismissed.Id}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.DeleteAsync("/api/data/transactions")).StatusCode.Should().Be(HttpStatusCode.OK);

        var item = (await StatementsAsync(client)).Single(s => s.GetProperty("id").GetGuid() == fulfilled.Id);
        item.GetProperty("status").GetString().Should().Be("awaitingUpload");
        item.GetProperty("title").GetString().Should().Be("CIBC chequing account ending 7890");
        (await RowAsync(userId, dismissed.Id))!.Status.Should().Be(StatementStatus.Dismissed);
    }

    [Fact]
    public async Task Two_dozen_uploads_in_a_row_are_accepted_and_all_processed()
    {
        var client = await factory.CreateDemoClientAsync();
        var started = new List<JsonElement>();

        for (var i = 0; i < 24; i++)
        {
            var month = (i % 12) + 1;
            var year = 2024 + (i / 12);
            started.Add(await client.UploadAsync(PdfStatementBuilder.Chequing(year, month, 1000m, [(5, $"COFFEE SHOP {i}", -4.50m)], institution: "CIBC")));
        }

        foreach (var upload in started)
        {
            var job = await client.WaitForJobAsync(upload.GetProperty("jobId").GetString()!);
            job.GetProperty("status").GetString().Should().Be("succeeded");
            job.GetProperty("steps")[0].GetProperty("status").GetString().Should().Be("done", job.ToString());
        }

        started.Select(u => u.GetProperty("statementId").GetGuid()).Should().OnlyHaveUniqueItems();
    }
}

public sealed class InstitutionsApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    private static readonly string[] InstitutionFields = ["id", "name", "signInUrl", "downloadHint"];

    [Fact]
    public async Task Lists_banks_sorted_by_name_with_unique_ids_and_https_links()
    {
        var client = await factory.CreateDemoClientAsync();

        var institutions = (await (await client.GetAsync("/api/institutions")).JsonAsync()).EnumerateArray().ToList();

        institutions.Should().HaveCount(KnownInstitutions.All.Count);
        institutions.Select(i => i.EnumerateObject().Select(p => p.Name)).Should().OnlyContain(names => names.SequenceEqual(InstitutionFields));
        var names = institutions.Select(i => i.GetProperty("name").GetString()!).ToList();
        names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase).And.OnlyHaveUniqueItems();
        institutions.Select(i => i.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();
        institutions.Select(i => i.GetProperty("signInUrl").GetString()).Where(u => u is not null)
            .Should().NotBeEmpty().And.OnlyContain(u => u!.StartsWith("https://", StringComparison.Ordinal));

        var cibc = institutions.Single(i => i.GetProperty("id").GetString() == "cibc");
        cibc.GetProperty("name").GetString().Should().Be("CIBC");
        cibc.GetProperty("signInUrl").GetString().Should().Be("https://www.cibconline.cibc.com");
        cibc.GetProperty("downloadHint").GetString().Should().Be("Sign in to CIBC Online Banking, open My documents, and download each month's statement as a PDF.");
        institutions.Should().Contain(i => i.GetProperty("signInUrl").ValueKind == JsonValueKind.Null, "banks without a known sign-in page are listed without a link");
    }

    [Fact]
    public async Task Requires_sign_in()
    {
        var response = await factory.CreateSessionClient().GetAsync("/api/institutions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
