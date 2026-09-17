using System.Globalization;
using FinSight.Core.Analytics;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Email;
using FinSight.Infrastructure.Insights;
using FinSight.Tests.TestHelpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FinSight.Tests.Automation;

public sealed class DigestScheduleTests
{
    [Theory]
    [InlineData("2026-09-01T12:00:00Z", null)]
    [InlineData("2026-09-02T23:59:59Z", null)]
    [InlineData("2026-09-03T00:00:00Z", "2026-08")]
    [InlineData("2026-09-30T12:00:00Z", "2026-08")]
    [InlineData("2027-01-05T08:00:00Z", "2026-12")]
    public void A_months_summary_is_due_from_the_third_of_the_next_month(string now, string? month)
    {
        var due = DigestSchedule.DueMonth(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        (due is null ? null : MonthlyDigest.MonthKeyOf(due.Start)).Should().Be(month);
        if (due is not null)
        {
            due.End.Should().Be(due.Start.AddMonths(1).AddDays(-1));
        }
    }
}

public sealed class UnsubscribeTokenTests
{
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _provider = new();

    [Fact]
    public void A_token_names_its_user_until_it_expires()
    {
        var tokens = new UnsubscribeTokens(_provider, _clock);
        var user = Guid.NewGuid();
        var token = tokens.Create(user);

        tokens.Read(token, out var read).Should().Be(UnsubscribeTokenStatus.Valid);
        read.Should().Be(user);

        _clock.Now += UnsubscribeTokens.Lifetime - TimeSpan.FromMinutes(1);
        tokens.Read(token, out _).Should().Be(UnsubscribeTokenStatus.Valid);

        _clock.Now += TimeSpan.FromMinutes(2);
        tokens.Read(token, out read).Should().Be(UnsubscribeTokenStatus.Expired);
        read.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Tampered_foreign_and_empty_tokens_are_rejected()
    {
        var tokens = new UnsubscribeTokens(_provider, _clock);
        var token = tokens.Create(Guid.NewGuid());
        var tampered = token[..^3] + (token[^3] == 'A' ? 'B' : 'A') + token[^2..];

        tokens.Read(tampered, out _).Should().Be(UnsubscribeTokenStatus.Invalid);
        tokens.Read(_provider.CreateProtector("FinSight.Fields.v1").Protect($"{Guid.NewGuid():N}.9999999999"), out _).Should().Be(UnsubscribeTokenStatus.Invalid,
            "a value protected for another purpose is not an unsubscribe token");
        tokens.Read(new UnsubscribeTokens(new EphemeralDataProtectionProvider(), _clock).Create(Guid.NewGuid()), out _).Should().Be(UnsubscribeTokenStatus.Invalid);
        tokens.Read("", out _).Should().Be(UnsubscribeTokenStatus.Invalid);
        tokens.Read(null, out _).Should().Be(UnsubscribeTokenStatus.Invalid);
    }
}

public sealed class PickupDirectoryEmailSenderTests
{
    [Fact]
    public async Task Writes_an_eml_with_html_and_plain_text_parts_and_the_given_headers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "finsight-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new PickupDirectoryEmailSender(directory, Options.Create(new EmailOptions { From = "finsight@example.com" }), TimeProvider.System);
            await sender.SendAsync(new EmailMessage("sam@example.com", "Subject", "<p>Hello</p>", "Hello",
                new Dictionary<string, string> { ["List-Unsubscribe"] = "<https://finsight.example.com/u>" }), CancellationToken.None);

            var file = Directory.GetFiles(directory, "*.eml").Should().ContainSingle().Subject;
            var message = await MimeKit.MimeMessage.LoadAsync(file);
            message.Headers["List-Unsubscribe"].Should().Be("<https://finsight.example.com/u>");
            message.TextBody.Should().Contain("Hello");
            message.HtmlBody.Should().Contain("<p>Hello</p>");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

public sealed class DigestTests
{
    private static readonly DateTimeOffset Sept2 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sept3 = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateRange August = new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
    private const string SecretDescription = "E-TRANSFER SENT JORDAN LEE REF 5566778899";

    private static async Task<AutomationApiFactory> StartAsync(DateTimeOffset now)
    {
        var factory = new AutomationApiFactory();
        factory.Clock.Now = now;
        _ = factory.Services;
        return factory;
    }

    private static Task<int> RunAsync(AutomationApiFactory factory, DigestService? service = null) =>
        (service ?? factory.Services.GetRequiredService<DigestService>()).RunDueAsync(CancellationToken.None);

    /// <summary>July and August transactions, including a description and account number that must never appear in email.</summary>
    private static Task<int> AddMonthsOfDataAsync(AutomationApiFactory factory, Guid userId) => factory.SystemAsync(async db =>
    {
        var statement = new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.ManualUpload,
            SourceKey = $"upload:{Guid.NewGuid():N}",
            Filename = "statement.pdf",
            Institution = "Maple Bank",
            AccountMask = "4821",
            AccountType = AccountType.Chequing,
            Status = StatementStatus.Processed,
            CreatedAt = Sept2,
            UpdatedAt = Sept2,
        };
        db.Statements.Add(statement);
        for (var month = 7; month <= 8; month++)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, month, 1), "PAYROLL ACME CORP", 5000m, "acme", "income.salary", TransactionType.Income));
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, month, 3), "RENT PAYMENT", -1800m, "landlord", "housing.rent"));
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, month, 10), "LOBLAWS #1234", month == 7 ? -400m : -520m, "loblaws", "food.groceries"));
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, month, 15), "NETFLIX.COM", -16.99m, "netflix", "entertainment.movies"));
        }

        db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 20), SecretDescription, -950m, "jordan-lee", "shopping.general"));
        db.Statements.Add(new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.Gmail,
            SourceKey = "gmail:m1:alert",
            Filename = "",
            Institution = "CIBC",
            AccountMask = "5190",
            Status = StatementStatus.AwaitingUpload,
            CreatedAt = Sept2,
            UpdatedAt = Sept2,
        });
        return await db.SaveChangesAsync();
    });

    private static Task<User> UserAsync(AutomationApiFactory factory, Guid id) => factory.SystemAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    [Fact]
    public async Task Sends_last_months_summary_from_the_third_and_never_twice_even_after_a_restart()
    {
        await using var factory = await StartAsync(Sept2);
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        (await RunAsync(factory)).Should().Be(0, "statements for August may still be arriving");
        factory.Email.Sent.Should().BeEmpty();

        factory.Clock.Now = Sept3;
        (await RunAsync(factory)).Should().Be(1);
        var message = factory.Email.Sent.Should().ContainSingle().Subject;
        message.To.Should().Be(user.Email);
        message.Subject.Should().Be("Your FinSight summary for August 2026");
        (await UserAsync(factory, user.Id)).LastDigestSentFor.Should().Be("2026-08");

        // Later ticks, a fresh service (as after a restart) and a later day in the month send nothing more.
        (await RunAsync(factory)).Should().Be(0);
        (await RunAsync(factory, ActivatorUtilities.CreateInstance<DigestService>(factory.Services))).Should().Be(0);
        factory.Clock.Now = Sept3.AddDays(5);
        (await RunAsync(factory)).Should().Be(0);
        factory.Email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Two_instances_sending_at_once_send_one_email()
    {
        await using var factory = await StartAsync(Sept3);
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        var first = ActivatorUtilities.CreateInstance<DigestService>(factory.Services);
        var second = ActivatorUtilities.CreateInstance<DigestService>(factory.Services);
        var sent = await Task.WhenAll(RunAsync(factory, first), RunAsync(factory, second));

        sent.Sum().Should().Be(1);
        factory.Email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_month_without_data_and_it_is_checked_again_the_next_day_not_every_tick()
    {
        await using var factory = await StartAsync(Sept3);
        var user = await factory.AddUserAsync("Empty", digest: true);

        (await RunAsync(factory)).Should().Be(0);
        factory.Email.Attempts.Should().Be(0);
        var saved = await UserAsync(factory, user.Id);
        saved.LastDigestSentFor.Should().BeNull();
        saved.DigestNextAttemptAt.Should().Be(Sept3 + DigestService.NoDataRetry);

        // August's statements arrive a few days late: the summary goes out on the next check.
        await AddMonthsOfDataAsync(factory, user.Id);
        (await RunAsync(factory)).Should().Be(0);
        factory.Clock.Now = Sept3 + DigestService.NoDataRetry;
        (await RunAsync(factory)).Should().Be(1);
    }

    [Fact]
    public async Task Only_opted_in_non_demo_users_get_summaries()
    {
        await using var factory = await StartAsync(Sept3);
        var optedIn = await factory.AddUserAsync("Yes", digest: true);
        var optedOut = await factory.AddUserAsync("No");
        var demo = await factory.AddUserAsync("Demo", demo: true, digest: true);
        foreach (var user in new[] { optedIn, optedOut, demo })
        {
            await AddMonthsOfDataAsync(factory, user.Id);
        }

        (await RunAsync(factory)).Should().Be(1);
        factory.Email.Sent.Should().ContainSingle().Which.To.Should().Be(optedIn.Email);
    }

    [Fact]
    public async Task Nothing_is_sent_when_email_is_not_configured()
    {
        await using var factory = await StartAsync(Sept3);
        factory.Email.IsConfigured = false;
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        (await RunAsync(factory)).Should().Be(0);
        factory.Email.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_on_later_ticks_up_to_the_cap()
    {
        await using var factory = await StartAsync(Sept3);
        factory.Email.Fail = true;
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        (await RunAsync(factory)).Should().Be(0);
        factory.Email.Attempts.Should().Be(1);
        var saved = await UserAsync(factory, user.Id);
        saved.LastDigestSentFor.Should().BeNull("a failed send doesn't count as sent");
        saved.DigestFailures.Should().Be(1);

        (await RunAsync(factory)).Should().Be(0);
        factory.Email.Attempts.Should().Be(1, "the retry waits for its backoff instead of running every tick");

        for (var tick = 0; tick < 20; tick++)
        {
            factory.Clock.Now += TimeSpan.FromHours(6);
            await RunAsync(factory);
        }

        factory.Email.Attempts.Should().Be(3);
        (await UserAsync(factory, user.Id)).LastDigestSentFor.Should().BeNull();

        // A later month starts a new allowance.
        factory.Email.Fail = false;
        factory.Clock.Now = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        await factory.SystemAsync(async db =>
        {
            var statement = await db.Statements.FirstAsync(s => s.UserId == user.Id && s.Source == StatementSourceKind.ManualUpload);
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 9, 12), "LOBLAWS", -80m, "loblaws", "food.groceries"));
            return await db.SaveChangesAsync();
        });
        (await RunAsync(factory)).Should().Be(1);
        (await UserAsync(factory, user.Id)).LastDigestSentFor.Should().Be("2026-09");
    }

    [Fact]
    public async Task The_summary_uses_the_dashboards_numbers_and_leaves_out_descriptions_and_account_numbers()
    {
        await using var factory = await StartAsync(Sept3);
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        var (digest, snapshot) = await factory.AsUserAsync(user.Id, async services =>
            (await services.GetRequiredService<DigestBuilder>().BuildAsync(August, CancellationToken.None),
             await services.GetRequiredService<DashboardService>().GetSnapshotAsync(August, CancellationToken.None)));

        digest.HasData.Should().BeTrue();
        digest.Income.Should().Be(snapshot.Summary.Income).And.Be(5000m);
        digest.Spending.Should().Be(snapshot.Summary.Expenses);
        digest.Net.Should().Be(snapshot.Summary.NetCashFlow);
        digest.SavingsRate.Should().Be(snapshot.Summary.SavingsRate);
        digest.TopCategories.Select(c => (c.Name, c.Amount, c.ChangePercent))
            .Should().Equal(snapshot.Summary.Categories.Take(3).Select(c => (c.Name, c.Amount, c.ChangePercent)));
        digest.AnomalyCount.Should().Be(snapshot.Anomalies.Count);
        digest.AwaitingUpload.Should().Be(1);
        digest.AiSummary.Should().BeNull("no analysis exists for August, and the summary never asks the AI for one");

        (await RunAsync(factory)).Should().Be(1);
        var message = factory.Email.Sent.Single();
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            body.Should().Contain(DigestRenderer.Money(snapshot.Summary.Income, "CAD"))
                .And.Contain(DigestRenderer.Money(snapshot.Summary.Expenses, "CAD"))
                .And.Contain(DigestRenderer.AmountsOnlyNote)
                .And.Contain("http://localhost:5173/")
                .And.NotContain(SecretDescription).And.NotContain("5566778899").And.NotContain("LOBLAWS #1234")
                .And.NotContain("4821").And.NotContain("5190");
        }

        message.TextBody.Should().NotContain("<", "the plain-text part is real text, not HTML");
        message.HtmlBody.Should().NotContain("<img").And.NotContain("url(");

        factory.Logs.Messages.Should().NotContain(m => m.Contains(user.Email, StringComparison.OrdinalIgnoreCase), "addresses are never logged");
    }

    [Fact]
    public async Task Includes_an_existing_ai_summary_for_the_month_but_never_generates_one()
    {
        await using var factory = await StartAsync(Sept3);
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);
        await factory.AsUserAsync(user.Id, async services =>
        {
            var db = services.GetRequiredService<FinSight.Infrastructure.Persistence.FinSightDbContext>();
            var snapshot = await services.GetRequiredService<DashboardService>().GetSnapshotAsync(August, CancellationToken.None);
            db.FinancialAnalyses.Add(new FinancialAnalysisRecord
            {
                UserId = user.Id,
                PeriodStart = August.Start,
                PeriodEnd = August.End,
                FactsHash = AnalysisService.BuildFacts(snapshot).Hash(),
                Model = "fake",
                ResultJson = System.Text.Json.JsonSerializer.Serialize(
                    new FinSight.Core.Insights.FinancialAnalysis("Spending rose with a large one-off purchase.", [], [], [], [], [], []), AnalysisService.Json),
                CorrectionsJson = "[]",
                CreatedAt = Sept3,
            });
            return await db.SaveChangesAsync();
        });

        (await RunAsync(factory)).Should().Be(1);
        factory.Email.Sent.Single().TextBody.Should().Contain("Spending rose with a large one-off purchase.");
    }

    [Fact]
    public async Task Every_summary_has_one_click_unsubscribe_headers_with_a_working_token()
    {
        await using var factory = await StartAsync(Sept3);
        var user = await factory.AddUserAsync("Sam", digest: true);
        await AddMonthsOfDataAsync(factory, user.Id);

        await RunAsync(factory);

        var message = factory.Email.Sent.Single();
        message.Headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        var header = message.Headers["List-Unsubscribe"];
        header.Should().StartWith("<http://localhost:5173/api/email/unsubscribe?token=").And.EndWith(">");
        message.HtmlBody.Should().Contain(System.Net.WebUtility.HtmlEncode(header[1..^1]));
        var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(header[1..^1]).Query)["token"].ToString();
        factory.Services.GetRequiredService<UnsubscribeTokens>().Read(token, out var tokenUser).Should().Be(UnsubscribeTokenStatus.Valid);
        tokenUser.Should().Be(user.Id);
    }

    [Fact]
    public void Merchant_names_are_html_encoded()
    {
        var digest = new MonthlyDigest(August, "CAD", true, 100m, 50m, 50m, 50m, [new DigestCategory("<b>Food</b>", 50m, 10m)],
            [new DigestRecurring("<script>alert(1)</script>", 9.99m)], [], 0, null, 0, 2, null);
        var rendered = DigestRenderer.Render(digest, new Uri("https://finsight.example.com/"), "https://finsight.example.com/api/email/unsubscribe?token=a&b", isTest: true);

        rendered.Subject.Should().Be("[Test] Your FinSight summary for August 2026");
        rendered.Html.Should().NotContain("<script>").And.Contain("&lt;script&gt;").And.Contain("token=a&amp;b");
        rendered.Text.Should().Contain("2 statements were found in Gmail and not imported yet");
    }
}

public sealed class EmailConfigurationTests
{
    [Fact]
    public void Production_without_a_public_url_or_smtp_reports_email_as_not_configured()
    {
        var environment = new TestEnvironment { EnvironmentName = Environments.Production };
        var links = new EmailLinks(Options.Create(new AppOptions()), environment);

        links.AppUrl.Should().BeNull();
        new EmailAvailability(new FakeEmailSender(), links).IsConfigured.Should().BeFalse("links need a public address");
        new EmailLinks(Options.Create(new AppOptions { PublicUrl = "http://finsight.example.com" }), environment).AppUrl.Should().BeNull("plain http is only for local addresses");
        new EmailAvailability(new FakeEmailSender { IsConfigured = false }, new EmailLinks(Options.Create(new AppOptions { PublicUrl = "https://finsight.example.com" }), environment))
            .IsConfigured.Should().BeFalse();
        new EmailAvailability(new FakeEmailSender(), new EmailLinks(Options.Create(new AppOptions { PublicUrl = "https://finsight.example.com/" }), environment))
            .IsConfigured.Should().BeTrue();
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "FinSight";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
