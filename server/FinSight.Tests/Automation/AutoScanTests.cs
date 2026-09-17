using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Automation;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Automation;

public sealed class AutoScanSchedulerTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static async Task<AutomationApiFactory> StartAsync()
    {
        var factory = new AutomationApiFactory();
        factory.Clock.Now = AutomationApiFactory.Start;
        _ = factory.Services;
        return factory;
    }

    private static Task<List<ProcessingJob>> JobsAsync(AutomationApiFactory factory, Guid userId) =>
        factory.SystemAsync(db => db.ProcessingJobs.AsNoTracking().Where(j => j.UserId == userId).ToListAsync());

    private static async Task<IReadOnlyList<AutoScanResult>> RunAsync(AutomationApiFactory factory) =>
        await factory.Services.GetRequiredService<AutoScanService>().RunDueAsync(CancellationToken.None);

    private static async Task WaitForJobsAsync(AutomationApiFactory factory, Guid userId)
    {
        for (var i = 0; i < 200; i++)
        {
            if ((await JobsAsync(factory, userId)).All(j => j.Status is JobStatus.Succeeded or JobStatus.Failed))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Jobs did not finish.");
    }

    [Fact]
    public void Each_user_gets_a_stable_slot_within_the_interval()
    {
        var user = Guid.NewGuid();
        var now = AutomationApiFactory.Start;

        var first = AutoScanService.NextSlot(user, now, Day);
        first.Should().BeAfter(now).And.BeOnOrBefore(now + Day);
        AutoScanService.NextSlot(user, first, Day).Should().Be(first + Day, "the next slot after a scan is the same time the next day");
        AutoScanService.NextSlot(user, first - TimeSpan.FromTicks(1), Day).Should().Be(first);
        Enumerable.Range(0, 20).Select(_ => AutoScanService.NextSlot(Guid.NewGuid(), now, Day)).Distinct().Should().HaveCountGreaterThan(15, "users are spread across the day");
    }

    [Fact]
    public async Task Scans_only_opted_in_users_with_an_active_gmail_connection_who_are_not_demo_users_and_only_when_due()
    {
        await using var factory = await StartAsync();
        var optedIn = await factory.AddUserAsync("Opted", autoScan: true, gmail: GmailConnectionStatus.Active);
        var notOptedIn = await factory.AddUserAsync("Manual", gmail: GmailConnectionStatus.Active);
        var noGmail = await factory.AddUserAsync("NoGmail", autoScan: true);
        var expired = await factory.AddUserAsync("Expired", autoScan: true, gmail: GmailConnectionStatus.Expired);
        var demo = await factory.AddUserAsync("Demo", demo: true, autoScan: true, gmail: GmailConnectionStatus.Active);

        // The first run only assigns daily slots: opting in doesn't scan straight away.
        (await RunAsync(factory)).Should().BeEmpty();
        var slot = await factory.SystemAsync(db => db.Users.Where(u => u.Id == optedIn.Id).Select(u => u.NextAutoScanAt).SingleAsync());
        slot.Should().NotBeNull().And.BeAfter(AutomationApiFactory.Start).And.BeOnOrBefore(AutomationApiFactory.Start + Day);

        factory.Clock.Now = slot!.Value - TimeSpan.FromMinutes(1);
        (await RunAsync(factory)).Should().BeEmpty("the scan isn't due yet");

        factory.Clock.Now = slot.Value;
        var results = await RunAsync(factory);

        results.Should().ContainSingle().Which.Should().Match<AutoScanResult>(r => r.UserId == optedIn.Id && r.Outcome == AutoScanOutcome.Scanned);
        var jobs = await JobsAsync(factory, optedIn.Id);
        jobs.Should().ContainSingle().Which.Should().Match<ProcessingJob>(j => j.Kind == JobKind.Sync && j.Status == JobStatus.Succeeded);
        foreach (var other in new[] { notOptedIn, noGmail, expired, demo })
        {
            (await JobsAsync(factory, other.Id)).Should().BeEmpty();
        }

        (await factory.SystemAsync(db => db.Users.Where(u => u.Id == optedIn.Id).Select(u => u.NextAutoScanAt).SingleAsync()))
            .Should().Be(slot.Value + Day, "the next scan is the same time tomorrow");
        (await RunAsync(factory)).Should().BeEmpty("a user is scanned once per slot");
    }

    [Fact]
    public async Task A_user_with_an_active_job_is_not_scanned_and_is_retried_later_rather_than_every_tick()
    {
        await using var factory = await StartAsync();
        var user = await factory.AddUserAsync("Busy", autoScan: true, gmail: GmailConnectionStatus.Active);
        await factory.SystemAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).NextAutoScanAt = factory.Clock.Now;
            db.ProcessingJobs.Add(new ProcessingJob { UserId = user.Id, Kind = JobKind.Process, Status = JobStatus.Running, CreatedAt = factory.Clock.Now });
            return await db.SaveChangesAsync();
        });

        var results = await RunAsync(factory);

        results.Should().ContainSingle().Which.Outcome.Should().Be(AutoScanOutcome.Busy);
        (await JobsAsync(factory, user.Id)).Should().ContainSingle("no scan job was added");
        (await RunAsync(factory)).Should().BeEmpty("the next attempt waits");
        (await factory.SystemAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.NextAutoScanAt).SingleAsync()))
            .Should().Be(factory.Clock.Now + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task Two_schedulers_running_at_once_never_scan_the_same_user_twice()
    {
        await using var factory = await StartAsync();
        var users = new List<User>();
        for (var i = 0; i < 4; i++)
        {
            users.Add(await factory.AddUserAsync($"User{i}", autoScan: true, gmail: GmailConnectionStatus.Active));
        }

        await factory.SystemAsync(db => db.Users.Where(u => u.Settings.AutoScanEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.NextAutoScanAt, factory.Clock.Now)));

        // Two independent instances, as on two servers sharing one database.
        var first = ActivatorUtilities.CreateInstance<AutoScanService>(factory.Services);
        var second = ActivatorUtilities.CreateInstance<AutoScanService>(factory.Services);
        var runs = await Task.WhenAll(first.RunDueAsync(CancellationToken.None), second.RunDueAsync(CancellationToken.None));

        runs.SelectMany(r => r).Count(r => r.Outcome == AutoScanOutcome.Scanned).Should().Be(4);
        foreach (var user in users)
        {
            (await JobsAsync(factory, user.Id)).Where(j => j.Kind == JobKind.Sync).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task A_revoked_gmail_grant_marks_the_connection_expired_and_is_not_retried()
    {
        await using var factory = await StartAsync();
        var user = await factory.AddUserAsync("Revoked", autoScan: true, gmail: GmailConnectionStatus.Active, refreshToken: "revoked");
        await factory.SystemAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.NextAutoScanAt, factory.Clock.Now)));

        (await RunAsync(factory)).Should().ContainSingle().Which.Outcome.Should().Be(AutoScanOutcome.Failed);
        (await factory.SystemAsync(db => db.GmailConnections.Where(c => c.UserId == user.Id).Select(c => c.Status).SingleAsync()))
            .Should().Be(GmailConnectionStatus.Expired);

        for (var day = 1; day <= 3; day++)
        {
            factory.Clock.Now += Day;
            (await RunAsync(factory)).Should().BeEmpty();
        }

        factory.RefreshTokensUsed.Should().ContainSingle("Google is asked once, not on every tick");
        (await JobsAsync(factory, user.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Scans_only_discover_unless_the_user_opted_in_to_import_from_accounts_they_already_imported()
    {
        await using var factory = await StartAsync();
        var importer = await factory.AddUserAsync("Importer", autoScan: true, autoImport: true, gmail: GmailConnectionStatus.Active);
        var discoverer = await factory.AddUserAsync("Discoverer", autoScan: true, gmail: GmailConnectionStatus.Active);
        foreach (var user in new[] { importer, discoverer })
        {
            await factory.SystemAsync(async db =>
            {
                db.Statements.Add(Processed(user.Id, "TD Bank", "7890"));
                (await db.Users.SingleAsync(u => u.Id == user.Id)).NextAutoScanAt = factory.Clock.Now;
                return await db.SaveChangesAsync();
            });
        }

        var received = factory.Clock.Now.AddDays(-2);
        factory.Gmail.Messages.Add(new EmailCandidate("known", "t1", "Your TD eStatement for account 1234567890 is ready", "TD Canada Trust <estatements@td.com>",
            "Your monthly statement is now available", received, [new EmailAttachment("2", "Statement.pdf", "application/pdf", 90_000)]));
        factory.Gmail.Messages.Add(new EmailCandidate("other", "t2", "Your TD eStatement for account 5555551111 is ready", "TD Canada Trust <estatements@td.com>",
            "Your monthly statement is now available", received, [new EmailAttachment("2", "Statement.pdf", "application/pdf", 90_000)]));
        factory.Gmail.Messages.Add(new EmailCandidate("nomask", "t3", "Your TD eStatement is ready", "TD Canada Trust <estatements@td.com>",
            "Your monthly statement is now available", received, [new EmailAttachment("2", "Statement.pdf", "application/pdf", 90_000)]));

        var results = await RunAsync(factory);

        results.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == AutoScanOutcome.Scanned);
        results.Single(r => r.UserId == discoverer.Id).ImportJobId.Should().BeNull();
        (await JobsAsync(factory, discoverer.Id)).Should().OnlyContain(j => j.Kind == JobKind.Sync);
        (await factory.SystemAsync(db => db.Statements.Where(s => s.UserId == discoverer.Id && s.Source == StatementSourceKind.Gmail).Select(s => s.Status).ToListAsync()))
            .Should().HaveCount(3).And.OnlyContain(s => s == StatementStatus.Discovered);

        var importJob = results.Single(r => r.UserId == importer.Id).ImportJobId;
        importJob.Should().NotBeNull();
        await WaitForJobsAsync(factory, importer.Id);
        var statements = await factory.SystemAsync(db => db.Statements.Where(s => s.UserId == importer.Id && s.Source == StatementSourceKind.Gmail).ToListAsync());
        statements.Single(s => s.SourceMessageId == "known").Status.Should().NotBe(StatementStatus.Discovered, "the matching account was picked up for import");
        statements.Single(s => s.SourceMessageId == "other").Status.Should().Be(StatementStatus.Discovered, "a different account at the same bank waits for the user");
        statements.Single(s => s.SourceMessageId == "nomask").Status.Should().Be(StatementStatus.Discovered, "without the last four digits nothing is assumed");
    }

    [Fact]
    public void Automatic_import_needs_the_same_institution_and_last_four_digits_and_a_real_statement()
    {
        var known = new[] { new KnownAccount("CIBC", "5190") };
        Statement Discovered(string? institution, string subject, DocumentKind kind = DocumentKind.CreditCardStatement, string key = "gmail:m:2") => new()
        {
            Source = StatementSourceKind.Gmail,
            SourceKey = key,
            Status = StatementStatus.Discovered,
            Institution = institution,
            Subject = subject,
            Filename = "statement.pdf",
            DocumentKind = kind,
        };

        var match = Discovered("cibc", "Your CIBC Visa statement for card ending in 5190");
        var candidates = new[]
        {
            match,
            Discovered("CIBC", "Your CIBC Visa statement for card ending in 4242"),
            Discovered("TD Bank", "Your statement for card ending in 5190"),
            Discovered("CIBC", "Your CIBC statement is ready"),
            Discovered("CIBC", "Pay stub ending in 5190", DocumentKind.IncomeDocument),
            Discovered("CIBC", "Statement ready for card ending in 5190", key: StatementAlert.SourceKey("m2")),
        };

        AutoImportRules.Select(candidates, known).Should().Equal(match);
    }

    private static Statement Processed(Guid userId, string institution, string mask) => new()
    {
        UserId = userId,
        Source = StatementSourceKind.ManualUpload,
        SourceKey = $"upload:{Guid.NewGuid():N}",
        Filename = "statement.pdf",
        Institution = institution,
        AccountMask = mask,
        AccountType = AccountType.Chequing,
        Status = StatementStatus.Processed,
        CreatedAt = AutomationApiFactory.Start.AddMonths(-1),
        UpdatedAt = AutomationApiFactory.Start.AddMonths(-1),
    };
}
