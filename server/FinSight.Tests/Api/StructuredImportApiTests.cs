using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Persistence;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Row = FinSight.Tests.TestHelpers.StatementFiles.OfxRow;

namespace FinSight.Tests.Api;

/// <summary>CSV, OFX and QFX files through the same upload pipeline as PDFs.</summary>
public sealed class StructuredImportApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    internal static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, byte[] bytes, string filename, Guid? statementId = null, string? password = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", filename);
        if (statementId is not null)
        {
            content.Add(new StringContent(statementId.Value.ToString()), "statementId");
        }

        if (password is not null)
        {
            content.Add(new StringContent(password), "password");
        }

        return await client.PostAsync("/api/uploads/statements", content);
    }

    /// <summary>Uploads a file and returns the finished job and the statement it created.</summary>
    internal static async Task<(JsonElement Job, Guid StatementId)> UploadFileAsync(HttpClient client, byte[] bytes, string filename, Guid? statementId = null, string? password = null)
    {
        var response = await PostFileAsync(client, bytes, filename, statementId, password);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var started = await response.JsonAsync();
        var job = await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!);
        return (job, started.GetProperty("statementId").GetGuid());
    }

    internal static JsonElement StatementStep(JsonElement job) => job.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("key").GetString()!.StartsWith("s:", StringComparison.Ordinal));

    private static async Task<JsonElement> StatementAsync(HttpClient client, Guid id) => await (await client.GetAsync($"/api/statements/{id}")).JsonAsync();

    [Fact]
    public async Task A_cibc_credit_card_csv_imports_like_a_statement()
    {
        var client = await factory.CreateDemoClientAsync();

        var (job, statementId) = await UploadFileAsync(client, StatementFiles.CibcCreditCardCsv(), "cibc.csv");

        job.GetProperty("status").GetString().Should().Be("succeeded", job.ToString());
        StatementStep(job).GetProperty("status").GetString().Should().Be("done");
        var detail = await StatementAsync(client, statementId);
        var statement = detail.GetProperty("statement");
        statement.GetProperty("format").GetString().Should().Be("csv");
        statement.GetProperty("institution").GetString().Should().Be("CIBC");
        statement.GetProperty("accountType").GetString().Should().Be("creditCard");
        statement.GetProperty("accountMask").GetString().Should().Be("5190");
        statement.GetProperty("documentKind").GetString().Should().Be("creditCardStatement");
        statement.GetProperty("title").GetString().Should().Be("Aug 3 – Aug 12, 2026");
        statement.GetProperty("periodEnd").GetString().Should().Be("2026-08-12");

        var transactions = detail.GetProperty("transactions").EnumerateArray().ToList();
        transactions.Select(t => t.GetProperty("amount").GetDecimal()).Should().BeEquivalentTo([-84.10m, 500m, -20.99m, -61.25m]);
        transactions.Single(t => t.GetProperty("amount").GetDecimal() == 500m).GetProperty("type").GetString().Should().Be("transfer", "a card payment is a transfer");
        transactions.Single(t => t.GetProperty("amount").GetDecimal() == -84.10m).GetProperty("categoryId").GetString().Should().NotBe("uncategorized");
    }

    [Fact]
    public async Task A_csv_that_doesnt_name_its_bank_gets_a_dated_title_and_never_clears_an_alert()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var alert = await StatementAlertFixtures.AddAsync(factory, userId, "CIBC", null, new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero), AccountType.Chequing);
        var csv = Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-08-01,PAYROLL ACME,3100.00\n2026-08-15,RENT,-1850.00\n");

        var (job, statementId) = await UploadFileAsync(client, csv, "export.csv");

        StatementStep(job).GetProperty("detail").GetString().Should().NotContain("alert");
        (await StatementAsync(client, statementId)).GetProperty("statement").GetProperty("title").GetString().Should().Be("CSV import · Aug 1 – Aug 15, 2026");
        (await StatementAlertFixtures.RowAsync(factory, userId, alert.Id))!.Status.Should().Be(StatementStatus.AwaitingUpload);
    }

    [Fact]
    public async Task A_csv_for_the_alerts_bank_and_card_clears_the_alert()
    {
        var client = await factory.CreateDemoClientAsync();
        var userId = await ApiTestData.UserIdAsync(client);
        var other = await StatementAlertFixtures.AddAsync(factory, userId, "CIBC", "1111", new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero), AccountType.CreditCard);
        var alert = await StatementAlertFixtures.AddAsync(factory, userId, "CIBC", "5190", new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero), AccountType.CreditCard);

        var (job, _) = await UploadFileAsync(client, StatementFiles.CibcCreditCardCsv(note: " ALERT"), "cibc-alert.csv");

        StatementStep(job).GetProperty("detail").GetString().Should().EndWith("statement alert cleared");
        (await StatementAlertFixtures.RowAsync(factory, userId, alert.Id))!.Status.Should().Be(StatementStatus.Dismissed);
        (await StatementAlertFixtures.RowAsync(factory, userId, other.Id))!.Status.Should().Be(StatementStatus.AwaitingUpload, "a different card");
    }

    [Fact]
    public async Task Ofx_reimports_and_overlapping_downloads_dedupe_by_the_banks_transaction_id()
    {
        var client = await factory.CreateDemoClientAsync();
        var account = $"00{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}";
        Row[] july = [new("20260728", -12.00m, "COFFEE ROASTERS", "T100"), new("20260803120000[-5:EST]", 3100m, "PAYROLL ACME CORP", "T101", "CREDIT")];

        var (first, firstId) = await UploadFileAsync(client, StatementFiles.Ofx1(account, "CHECKING", july, start: "20260720", end: "20260805"), "july.ofx");
        first.GetProperty("status").GetString().Should().Be("succeeded");
        (await StatementAsync(client, firstId)).GetProperty("statement").GetProperty("format").GetString().Should().Be("ofx");

        // A later download overlaps: the same two transactions (the bank renamed one) and two new ones, saved as .qfx.
        Row[] august = [july[0] with { Name = "COFFEE ROASTERS #2" }, july[1], new("20260809", -20.99m, "NETFLIX.COM", "T102"), new("20260815", -1850m, "RENT", "T103")];
        var (second, secondId) = await UploadFileAsync(client, StatementFiles.Ofx1(account, "CHECKING", august, start: "20260725", end: "20260831"), "august.qfx");

        StatementStep(second).GetProperty("detail").GetString().Should().StartWith("2 transactions");
        var detail = await StatementAsync(client, secondId);
        detail.GetProperty("statement").GetProperty("format").GetString().Should().Be("qfx");
        detail.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.StartsWith("2 transaction(s) were already imported", StringComparison.Ordinal));

        // Uploading the identical file again reprocesses the same statement without duplicating anything.
        var (again, againId) = await UploadFileAsync(client, StatementFiles.Ofx1(account, "CHECKING", august, start: "20260725", end: "20260831"), "august-copy.qfx");
        againId.Should().Be(secondId);
        StatementStep(again).GetProperty("detail").GetString().Should().StartWith("2 transactions");
        (await client.TransactionsAsync()).Count(t => t.GetProperty("account").GetProperty("mask").GetString() == account[^4..]).Should().Be(4);
    }

    [Fact]
    public async Task Full_card_and_account_numbers_are_never_stored()
    {
        var client = await factory.CreateDemoClientAsync();
        const string card = "4111111111111111";
        var csv = Encoding.UTF8.GetBytes($"Card Number,Transaction Date,Description,Amount\n{card},2026-07-04,PAID WITH {card},-4.50\n{card},2026-07-05,BOOKSTORE,-30.00\n");
        const string account = "000412345678901234";

        var (csvJob, csvId) = await UploadFileAsync(client, csv, $"card {card}.csv");
        var (ofxJob, _) = await UploadFileAsync(client, StatementFiles.Ofx1(account, "SAVINGS", [new("20260704", 1.25m, $"INTEREST {account}", "S1", "CREDIT")]), "savings.ofx");

        csvJob.GetProperty("status").GetString().Should().Be("succeeded");
        ofxJob.GetProperty("status").GetString().Should().Be("succeeded");
        var csvStatement = (await StatementAsync(client, csvId)).GetProperty("statement");
        csvStatement.GetProperty("accountMask").GetString().Should().Be("1111");
        csvStatement.GetProperty("filename").GetString().Should().NotContain(card);

        var everything = await (await client.GetAsync("/api/statements")).Content.ReadAsStringAsync()
            + await (await client.GetAsync("/api/transactions?pageSize=200")).Content.ReadAsStringAsync();
        everything.Should().NotContain(card).And.NotContain(account);
        DatabaseText(factory).Should().NotContain(card).And.NotContain(account).And.NotContain("12345678901234");
    }

    [Fact]
    public async Task Files_that_arent_statements_are_refused_before_they_are_queued()
    {
        var client = await factory.CreateDemoClientAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

        var image = await PostFileAsync(client, png, "statement.csv");
        var text = await PostFileAsync(client, "just some notes"u8.ToArray(), "notes.txt");

        image.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await image.ErrorCodeAsync()).Should().Be("unsupported_file");
        (await text.ErrorCodeAsync()).Should().Be("unsupported_file");
        (await (await client.GetAsync("/api/jobs/active")).Content.ReadAsStringAsync()).Should().NotContain("upload");
    }

    [Fact]
    public async Task A_csv_whose_columns_cant_be_mapped_fails_with_a_clear_code()
    {
        var client = await factory.CreateDemoClientAsync();

        var (job, statementId) = await UploadFileAsync(client, "Reference,Memo,Note\nABC,hello,world\nDEF,more,words\n"u8.ToArray(), "odd.csv");

        var step = StatementStep(job);
        step.GetProperty("status").GetString().Should().Be("failed");
        step.GetProperty("code").GetString().Should().Be("csv_unrecognized");
        step.GetProperty("detail").GetString().Should().Contain("couldn't tell which columns");
        var statement = (await StatementAsync(client, statementId)).GetProperty("statement");
        statement.GetProperty("failureCode").GetString().Should().Be("csv_unrecognized");
        statement.GetProperty("format").GetString().Should().Be("csv");
    }

    internal static string DatabaseText(FinSightApiFactory factory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var text = new StringBuilder();
        foreach (var path in new[] { factory.DatabasePath, factory.DatabasePath + "-wal" }.Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            text.Append(Encoding.Latin1.GetString(buffer.ToArray()));
            text.Append(Encoding.Unicode.GetString(buffer.ToArray()));
        }

        return text.ToString();
    }
}

internal static class StatementAlertFixtures
{
    public static async Task<Statement> AddAsync(FinSightApiFactory factory, Guid userId, string institution, string? mask, DateTimeOffset received, AccountType type)
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

    public static async Task<Statement?> RowAsync(FinSightApiFactory factory, Guid userId, Guid statementId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        return await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Statements.AsNoTracking().SingleOrDefaultAsync(s => s.Id == statementId);
    }
}
