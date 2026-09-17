using System.Net;
using FinSight.Core.Abstractions;
using FinSight.Infrastructure.Pdf;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>The API with every log message captured.</summary>
public sealed class LogCapturingApiFactory : FinSightApiFactory
{
    internal CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
}

/// <summary>Password-protected PDFs: the password opens the file for one read and is never kept anywhere.</summary>
public sealed class PdfPasswordApiTests(LogCapturingApiFactory factory) : IClassFixture<LogCapturingApiFactory>
{
    [Fact]
    public async Task A_locked_pdf_asks_for_its_password_then_a_wrong_one_is_refused_and_the_right_one_unlocks_it()
    {
        var client = await factory.CreateDemoClientAsync();
        var password = $"Dob-{Guid.NewGuid():N}";
        var pdf = EncryptedPdfBuilder.Chequing(password);

        var (locked, statementId) = await StructuredImportApiTests.UploadFileAsync(client, pdf, "locked.pdf");
        var lockedStep = StructuredImportApiTests.StatementStep(locked);
        lockedStep.GetProperty("status").GetString().Should().Be("failed");
        lockedStep.GetProperty("code").GetString().Should().Be("pdf_password_protected");
        lockedStep.GetProperty("detail").GetString().Should().Be("This PDF is password-protected. Enter its password to unlock it.");

        var (wrong, wrongId) = await StructuredImportApiTests.UploadFileAsync(client, pdf, "locked.pdf", statementId, "not-the-password");
        wrongId.Should().Be(statementId);
        StructuredImportApiTests.StatementStep(wrong).GetProperty("code").GetString().Should().Be("pdf_password_incorrect");
        var failed = await (await client.GetAsync($"/api/statements/{statementId}")).JsonAsync();
        failed.GetProperty("statement").GetProperty("failureCode").GetString().Should().Be("pdf_password_incorrect");

        var (unlocked, unlockedId) = await StructuredImportApiTests.UploadFileAsync(client, pdf, "locked.pdf", statementId, password);
        unlockedId.Should().Be(statementId);
        unlocked.GetProperty("status").GetString().Should().Be("succeeded");
        StructuredImportApiTests.StatementStep(unlocked).GetProperty("detail").GetString().Should().Be("3 transactions");
        var detail = await (await client.GetAsync($"/api/statements/{statementId}")).JsonAsync();
        detail.GetProperty("statement").GetProperty("status").GetString().Should().Be("processed");
        detail.GetProperty("statement").GetProperty("failureCode").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        detail.GetProperty("statement").GetProperty("format").GetString().Should().Be("pdf");
    }

    [Fact]
    public async Task The_password_is_never_stored_logged_or_returned()
    {
        var client = await factory.CreateDemoClientAsync();
        var password = $"Secret{Guid.NewGuid():N}";
        var pdf = EncryptedPdfBuilder.Chequing(password);

        var (wrongJob, statementId) = await StructuredImportApiTests.UploadFileAsync(client, pdf, "locked.pdf", password: password + "-typo");
        var (job, _) = await StructuredImportApiTests.UploadFileAsync(client, pdf, "locked.pdf", statementId, password);
        job.GetProperty("status").GetString().Should().Be("succeeded");

        var responses = string.Join('\n',
            wrongJob.ToString(),
            job.ToString(),
            await (await client.GetAsync("/api/statements")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/statements/{statementId}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/transactions?pageSize=200")).Content.ReadAsStringAsync());
        responses.Should().NotContain(password);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(await ApiTestData.UserIdAsync(client));
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            var jobs = await db.ProcessingJobs.AsNoTracking().ToListAsync();
            jobs.Should().NotBeEmpty();
            jobs.Should().OnlyContain(j => !j.StepsJson.Contains(password) && (j.ErrorCode == null || !j.ErrorCode.Contains(password)));
            var statement = await db.Statements.AsNoTracking().SingleAsync(s => s.Id == statementId);
            System.Text.Json.JsonSerializer.Serialize(statement).Should().NotContain(password);
        }

        StructuredImportApiTests.DatabaseText(factory).Should().NotContain(password);
        factory.Logs.Messages.Should().NotBeEmpty();
        factory.Logs.Messages.Should().NotContain(m => m.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Passwords_are_bounded_and_ignored_for_files_that_arent_pdfs()
    {
        var client = await factory.CreateDemoClientAsync();

        var tooLong = await StructuredImportApiTests.PostFileAsync(client, PdfStatementBuilder.Blank(), "a.pdf", password: new string('x', 257));
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooLong.ErrorCodeAsync()).Should().Be("password_too_long");
        (await tooLong.Content.ReadAsStringAsync()).Should().NotContain(new string('x', 257));

        var (csvJob, _) = await StructuredImportApiTests.UploadFileAsync(client, StatementFiles.CibcCreditCardCsv(note: " PWD"), "card.csv", password: "unused");
        csvJob.GetProperty("status").GetString().Should().Be("succeeded");
    }
}

public sealed class PdfPasswordExtractorTests
{
    [Fact]
    public void Encrypted_pdfs_need_the_right_password()
    {
        var pdf = EncryptedPdfBuilder.Chequing("letmein-1990");
        var extractor = new PdfPigTextExtractor();

        extractor.Invoking(e => e.Extract(pdf)).Should().Throw<PdfPasswordRequiredException>();
        extractor.Invoking(e => e.Extract(pdf, "")).Should().Throw<PdfPasswordRequiredException>();
        extractor.Invoking(e => e.Extract(pdf, "letmein-1991")).Should().Throw<PdfPasswordIncorrectException>()
            .Which.ToString().Should().NotContain("letmein");

        var text = extractor.Extract(pdf, "letmein-1990");
        text.Pages.Single().Words.Select(w => w.Text).Should().Contain(["NETFLIX.COM", "Withdrawals"]);
    }

    [Fact]
    public void Uploaded_files_wipe_their_bytes_and_drop_the_password_when_cleared()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var upload = new UploadedFile(bytes, "hunter2");
        var item = new JobWorkItem(Guid.NewGuid(), Guid.NewGuid(), FinSight.Core.Domain.JobKind.Upload, [Guid.Empty], new Dictionary<Guid, UploadedFile> { [Guid.Empty] = upload });

        upload.ToString().Should().NotContain("hunter2");
        item.ToString().Should().NotContain("hunter2");
        JobQueue.UploadBytesOf(item).Should().Be(4);

        item.ClearUploads();

        bytes.Should().OnlyContain(b => b == 0);
        upload.Content.Should().BeEmpty();
        upload.Password.Should().BeNull();
        JobQueue.UploadBytesOf(item).Should().Be(4, "the reservation is released in full after clearing");
    }

    [Fact]
    public async Task The_worker_clears_an_upload_once_its_job_has_run()
    {
        await using var factory = new LogCapturingApiFactory();
        var client = await factory.CreateDemoClientAsync();
        var queue = factory.Services.GetRequiredService<JobQueue>();
        var upload = new UploadedFile(EncryptedPdfBuilder.Chequing("pw-12345"), "pw-12345");
        var userId = await ApiTestData.UserIdAsync(client);

        Guid jobId;
        Guid statementId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            var now = DateTimeOffset.UtcNow;
            var statement = new FinSight.Core.Domain.Statement
            {
                UserId = userId, Source = FinSight.Core.Domain.StatementSourceKind.ManualUpload, SourceKey = $"upload:{Guid.NewGuid():N}", Filename = "locked.pdf",
                Status = FinSight.Core.Domain.StatementStatus.Discovered, CreatedAt = now, UpdatedAt = now,
            };
            db.Statements.Add(statement);
            await db.SaveChangesAsync();
            statementId = statement.Id;
            var job = await scope.ServiceProvider.GetRequiredService<JobService>()
                .CreateAsync(userId, FinSight.Core.Domain.JobKind.Upload, StatementLabels.ProcessingPlan([statement]), CancellationToken.None);
            jobId = job.Id;
        }

        queue.TryReserveUploadBytes(upload.ReservedBytes).Should().BeTrue();
        await queue.EnqueueAsync(new JobWorkItem(jobId, userId, FinSight.Core.Domain.JobKind.Upload, [statementId], new Dictionary<Guid, UploadedFile> { [statementId] = upload }), CancellationToken.None);
        var finished = await client.WaitForJobAsync(jobId.ToString());

        finished.GetProperty("status").GetString().Should().Be("succeeded");
        for (var i = 0; i < 100 && upload.Password is not null; i++)
        {
            await Task.Delay(20);
        }

        upload.Password.Should().BeNull();
        upload.Content.Should().BeEmpty();
    }
}
