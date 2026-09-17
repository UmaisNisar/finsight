using FinSight.Core.Abstractions;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Demo;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Pdf;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinSight.Tests.Infrastructure;

public sealed class CategorizationServiceTests
{
    private static readonly DateOnly Day = new(2026, 8, 12);

    private static CategorizationService Service(FinSightDbContext db, IGeminiService gemini) =>
        new(db, gemini, TimeProvider.System, NullLogger<CategorizationService>.Instance);

    [Fact]
    public async Task Ai_answers_apply_only_to_money_moving_in_the_direction_they_were_asked_about()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService
        {
            // The same merchant pays the user (consulting) and charges them (software).
            Categorize = requests => requests.Select(r => r.Direction == MerchantDirection.In
                ? new MerchantCategorization(r.MerchantKey, r.Direction, "income.freelance", TransactionType.Income, "Zxq", 0.9, "Consulting")
                : new MerchantCategorization(r.MerchantKey, r.Direction, "shopping.general", TransactionType.Expense, "Zxq", 0.8, "Software")).ToList(),
        };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.AddRange(
                TestDb.NewTransaction(statement, Day, "ZXQ HOLDINGS", 1200m, "zxqholdings", "income.other", TransactionType.Income, CategorySource.Default, 0.3),
                TestDb.NewTransaction(statement, Day, "ZXQ HOLDINGS", -49m, "zxqholdings"));
            await db.SaveChangesAsync();

            var outcome = await Service(db, gemini).CategorizeWithAiAsync([statement.Id], CancellationToken.None);

            outcome.Failure.Should().BeNull();
            gemini.CategorizationCalls.Single().Should().HaveCount(2);
            var income = await db.Transactions.SingleAsync(t => t.Amount > 0);
            income.CategoryId.Should().Be("income.freelance");
            income.Type.Should().Be(TransactionType.Income);
            income.IsRefund.Should().BeFalse();
            var spending = await db.Transactions.SingleAsync(t => t.Amount < 0);
            spending.CategoryId.Should().Be("shopping.general");
            spending.Type.Should().Be(TransactionType.Expense);

            // The cached rule describes spending, and never turns a payment out into income.
            (await db.MerchantRules.SingleAsync()).CategoryId.Should().Be("shopping.general");
        }
    }

    [Fact]
    public async Task Ai_results_are_cached_as_rules_so_a_merchant_is_only_sent_once()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService
        {
            Categorize = requests => requests.Select(r => new MerchantCategorization(r.MerchantKey, r.Direction, "food.coffee", TransactionType.Expense, "Qwerty Beans", 0.9, "Cafe")).ToList(),
        };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, Day, "QWERTY BEANS", -6m, "qwertybeans"));
            await db.SaveChangesAsync();
            await Service(db, gemini).CategorizeWithAiAsync([statement.Id], CancellationToken.None);

            var later = TestDb.NewTransaction(statement, Day.AddDays(7), "QWERTY BEANS", -7m, "qwertybeans");
            await Service(db, gemini).ApplyRulesAsync([later], AccountType.CreditCard, CancellationToken.None);

            later.CategoryId.Should().Be("food.coffee");
            later.CategorySource.Should().Be(CategorySource.Ai);
            gemini.CategorizationCalls.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Ai_never_overrides_a_users_merchant_rule_or_edited_transactions()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService
        {
            Categorize = requests => requests.Select(r => new MerchantCategorization(r.MerchantKey, r.Direction, "shopping.general", TransactionType.Expense, null, 0.9, "Guess")).ToList(),
        };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.MerchantRules.Add(new MerchantRule { UserId = user.Id, MerchantKey = "corner", CategoryId = "food.groceries", Source = CategorySource.User, Confidence = 0.5 });
            var edited = TestDb.NewTransaction(statement, Day, "ASDF", -9m, "asdf");
            edited.UserCategoryId = "health.pharmacy";
            db.Transactions.AddRange(TestDb.NewTransaction(statement, Day, "CORNER", -30m, "corner"), edited);
            await db.SaveChangesAsync();

            await Service(db, gemini).CategorizeWithAiAsync([statement.Id], CancellationToken.None);

            (await db.MerchantRules.SingleAsync()).CategoryId.Should().Be("food.groceries");
            gemini.CategorizationCalls.Single().Select(r => r.MerchantKey).Should().NotContain("asdf");
        }
    }

    [Fact]
    public async Task When_ai_is_unavailable_rule_categories_stay_and_the_failure_is_reported()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService { Categorize = _ => throw new AiUnavailableException(AiFailure.RateLimited) };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, Day, "UNKNOWN SHOP", -12m, "unknownshop"));
            await db.SaveChangesAsync();

            var outcome = await Service(db, gemini).CategorizeWithAiAsync([statement.Id], CancellationToken.None);

            outcome.Failure.Should().Be(AiFailure.RateLimited);
            (await db.Transactions.SingleAsync()).CategoryId.Should().Be(CategoryTaxonomy.Uncategorized);
            (await db.MerchantRules.AnyAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Pays_card_from_bank_as_a_matched_transfer_and_releases_it_when_one_side_is_deleted()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var bank = await testDb.AddStatementAsync(user.Id, "Maple Bank", AccountType.Chequing, "4821");
        var card = await testDb.AddStatementAsync(user.Id, "Harbour Card", AccountType.CreditCard, "4417");

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var service = Service(db, new FakeGeminiService());
            var payment = TestDb.NewTransaction(bank, Day, "ONLINE BANKING PAYMENT HARBOUR CARD", -812.40m, "onlinebankingpaymentharbourcard");
            var received = TestDb.NewTransaction(card, Day.AddDays(1), "PAYMENT - THANK YOU", 812.40m, "paymentthankyou");
            await service.ApplyRulesAsync([payment], AccountType.Chequing, CancellationToken.None);
            await service.ApplyRulesAsync([received], AccountType.CreditCard, CancellationToken.None);
            var ruleCategory = payment.CategoryId;
            db.Transactions.AddRange(payment, received);
            await db.SaveChangesAsync();

            (await service.MatchTransfersAsync(Day.AddDays(-30), Day.AddDays(30), CancellationToken.None)).Should().Be(1);
            payment.Type.Should().Be(TransactionType.Transfer);
            payment.CategoryId.Should().Be(CategoryTaxonomy.CreditCardPayments);
            payment.TransferPairId.Should().Be(received.TransferPairId).And.NotBeNull();

            // Without the card statement, the payment is categorized exactly as if the bank statement stood alone.
            await db.Statements.Where(s => s.Id == card.Id).ExecuteDeleteAsync();
            db.ChangeTracker.Clear();
            (await service.ReleaseOrphanedTransfersAsync(CancellationToken.None)).Should().Be(1);

            var orphan = await db.Transactions.SingleAsync();
            orphan.TransferPairId.Should().BeNull();
            orphan.CategoryId.Should().Be(ruleCategory);
            orphan.Type.Should().NotBe(TransactionType.Transfer);
            (await service.ReleaseOrphanedTransfersAsync(CancellationToken.None)).Should().Be(0);
        }
    }
}

public sealed class StatementImportServiceTests
{
    private static readonly (int, string, decimal)[] AugustRows =
    [
        (3, "PAYROLL DEPOSIT ACME CORP", 3100m),
        (4, "NETFLIX.COM", -20.99m),
        (15, "PRE-AUTHORIZED DEBIT MAPLE RESIDENTIAL RENT", -1850m),
    ];

    private static StatementImportService Importer(FinSightDbContext db) =>
        new(db, new PdfPigTextExtractor(), new CategorizationService(db, new FakeGeminiService(), TimeProvider.System, NullLogger<CategorizationService>.Instance), TimeProvider.System);

    private static async Task<ImportResult> ImportAsync(TestDb testDb, Guid userId, Guid statementId, byte[] pdf)
    {
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            var statement = await db.Statements.SingleAsync(s => s.Id == statementId);
            return await Importer(db).ImportAsync(statement, pdf, "CAD", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Imports_extracts_metadata_and_categorizes()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id, institution: null!, AccountType.Unknown, mask: null!);

        var result = await ImportAsync(testDb, user.Id, statement.Id, PdfStatementBuilder.Chequing(2026, 8, 1000m, AugustRows));

        result.Outcome.Should().Be(ImportOutcome.Imported);
        result.TransactionCount.Should().Be(3);
        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var saved = await db.Statements.SingleAsync();
            saved.Status.Should().Be(StatementStatus.Processed);
            saved.AccountMask.Should().Be("7890");
            saved.PeriodStart.Should().Be(new DateOnly(2026, 8, 1));
            saved.ContentHash.Should().HaveLength(64);
            (await db.Transactions.SingleAsync(t => t.Amount == -1850m)).CategoryId.Should().Be("housing.rent");
        }
    }

    [Fact]
    public async Task The_same_file_on_another_statement_is_recognised_as_a_duplicate()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var first = await testDb.AddStatementAsync(user.Id);
        var second = await testDb.AddStatementAsync(user.Id);
        var pdf = PdfStatementBuilder.Chequing(2026, 8, 1000m, AugustRows);

        await ImportAsync(testDb, user.Id, first.Id, pdf);
        var result = await ImportAsync(testDb, user.Id, second.Id, pdf);

        result.Outcome.Should().Be(ImportOutcome.DuplicateFile);
        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            (await db.Transactions.CountAsync()).Should().Be(3);
        }
    }

    [Fact]
    public async Task Transactions_already_imported_from_an_overlapping_statement_are_skipped()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var first = await testDb.AddStatementAsync(user.Id);
        var second = await testDb.AddStatementAsync(user.Id);

        await ImportAsync(testDb, user.Id, first.Id, PdfStatementBuilder.Chequing(2026, 8, 1000m, AugustRows));
        var result = await ImportAsync(testDb, user.Id, second.Id,
            PdfStatementBuilder.Chequing(2026, 8, 1000m, [.. AugustRows, (28, "ENBRIDGE GAS DISTRIBUTION", -61.20m)], note: "Corrected copy"));

        result.Outcome.Should().Be(ImportOutcome.Imported);
        result.TransactionCount.Should().Be(1);
        result.SkippedDuplicates.Should().Be(3);
    }

    [Fact]
    public async Task Reprocessing_replaces_transactions_but_keeps_user_edits()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var pdf = PdfStatementBuilder.Chequing(2026, 8, 1000m, AugustRows);
        await ImportAsync(testDb, user.Id, statement.Id, pdf);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var netflix = await db.Transactions.SingleAsync(t => t.Amount == -20.99m);
            netflix.UserCategoryId = "personal.education";
            netflix.UserMerchant = "Family plan";
            netflix.IsExcluded = true;
            await db.SaveChangesAsync();
        }

        (await ImportAsync(testDb, user.Id, statement.Id, pdf)).Outcome.Should().Be(ImportOutcome.Imported);

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.CountAsync()).Should().Be(3);
            var netflix = await reader.Transactions.SingleAsync(t => t.Amount == -20.99m);
            netflix.UserCategoryId.Should().Be("personal.education");
            netflix.UserMerchant.Should().Be("Family plan");
            netflix.IsExcluded.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("unreadable", StatementFailure.Unreadable)]
    [InlineData("scanned", StatementFailure.NoTextLayer)]
    public async Task Files_that_cannot_be_read_fail_with_a_stable_code(string kind, string expectedCode)
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var pdf = kind == "scanned" ? PdfStatementBuilder.Blank() : "%PDF-1.7\n this is not really a pdf"u8.ToArray();

        var result = await ImportAsync(testDb, user.Id, statement.Id, pdf);

        result.Outcome.Should().Be(ImportOutcome.Failed);
        result.FailureCode.Should().Be(expectedCode);
        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var saved = await db.Statements.SingleAsync();
            saved.Status.Should().Be(StatementStatus.Failed);
            saved.FailureCode.Should().Be(expectedCode);
        }
    }
}

public sealed class StatementDiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task ConnectGmailAsync(TestDb testDb, Guid userId)
    {
        var (setup, _) = testDb.Context(userId);
        await using (setup)
        {
            setup.GmailConnections.Add(new GmailConnection
            {
                UserId = userId,
                GoogleEmail = "sam@example.com",
                EncryptedRefreshToken = ((ITokenProtector)testDb.Protector).Protect("refresh"),
                Scopes = GoogleIntegrationOptions.GmailReadonlyScope,
            });
            await setup.SaveChangesAsync();
        }
    }

    private static async Task<DiscoveryResult> DiscoverAsync(TestDb testDb, Guid userId, FakeGmailClient gmail)
    {
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            var http = new StubHttpHandler().Json("""{ "access_token": "a", "expires_in": 3600 }""");
            var tokens = new GoogleTokenService(new HttpClient(http), db, testDb.Protector, new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new GoogleIntegrationOptions { ClientId = "c", ClientSecret = "s" }), NullLogger<GoogleTokenService>.Instance);
            var service = new StatementDiscoveryService(db, tokens, gmail, Options.Create(new GoogleIntegrationOptions()), new MutableTimeProvider(Now));
            return await service.DiscoverAsync(userId, CancellationToken.None);
        }
    }

    private static EmailCandidate CibcAlert(string messageId, string mask, DateTimeOffset received) =>
        new(messageId, $"t-{messageId}", "eStatement Alert", "CIBC Banking <mailbox.noreply@cibc.com>",
            $"Hi Sam Rivera, Your eStatement for the current month for your CIBC credit card ending in {mask} is now available. "
            + "Please sign on to CIBC Mobile Banking or CIBC Online Banking and go to My documents for details.",
            received, []);

    [Fact]
    public async Task Finds_statement_emails_masks_what_it_stores_and_is_idempotent()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);

        var gmail = new FakeGmailClient();
        gmail.Messages.Add(new EmailCandidate("m1", "t1", "Your TD eStatement for account 1234567890 is ready", "TD Canada Trust <estatements@td.com>",
            "Your monthly statement is now available", Now.AddDays(-5), [new EmailAttachment("2", "Statement_1234567890.pdf", "application/pdf", 90_000)]));
        gmail.Messages.Add(new EmailCandidate("m2", "t2", "Your order receipt", "Shop <orders@shop.example>",
            "Thanks for your order", Now.AddDays(-3), [new EmailAttachment("1", "receipt.pdf", "application/pdf", 20_000)]));

        var first = await DiscoverAsync(testDb, user.Id, gmail);
        var second = await DiscoverAsync(testDb, user.Id, gmail);

        first.NewStatements.Should().Be(1);
        second.NewStatements.Should().Be(0);
        second.TotalStatements.Should().Be(1);

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            var statement = await reader.Statements.SingleAsync();
            statement.SourceKey.Should().Be("gmail:m1:2");
            statement.Status.Should().Be(StatementStatus.Discovered);
            statement.Subject.Should().NotContain("1234567890").And.Contain("7890");
            statement.Filename.Should().NotContain("1234567890");
            (await reader.GmailConnections.SingleAsync()).LastSyncedAt.Should().Be(Now);
        }
    }

    [Fact]
    public async Task Records_statement_alerts_as_awaiting_upload_without_duplicates_or_the_message_text()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);
        var gmail = new FakeGmailClient();
        gmail.Messages.Add(CibcAlert("a1", "5190", Now.AddDays(-2)));
        gmail.Messages.Add(new EmailCandidate("p1", "t-p1", "Your payment is due soon", "CIBC Banking <mailbox.noreply@cibc.com>",
            "Your minimum payment for your credit card ending in 5190 is due Oct 5.", Now.AddDays(-1), []));
        gmail.Messages.Add(new EmailCandidate("s1", "t-s1", "Your TD eStatement is ready", "TD Canada Trust <estatements@td.com>",
            "Your monthly statement is attached", Now.AddDays(-3), [new EmailAttachment("2", "Statement.pdf", "application/pdf", 90_000)]));

        var first = await DiscoverAsync(testDb, user.Id, gmail);
        var second = await DiscoverAsync(testDb, user.Id, gmail);

        first.NewStatements.Should().Be(1);
        first.NewAlerts.Should().Be(1);
        second.NewStatements.Should().Be(0);
        second.NewAlerts.Should().Be(0);
        gmail.Queries.Should().Contain(q => q.Contains("cibc.com", StringComparison.Ordinal) && !q.Contains("has:attachment", StringComparison.Ordinal));

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            var alert = await reader.Statements.SingleAsync(s => s.SourceMessageId == "a1");
            alert.SourceKey.Should().Be("gmail:a1:alert");
            alert.SourcePartId.Should().BeNull();
            alert.Status.Should().Be(StatementStatus.AwaitingUpload);
            alert.Institution.Should().Be("CIBC");
            alert.AccountType.Should().Be(AccountType.CreditCard);
            alert.AccountMask.Should().Be("5190");
            alert.ReceivedAt.Should().Be(Now.AddDays(-2));
            alert.Filename.Should().BeEmpty();
            alert.DocumentKind.Should().Be(DocumentKind.CreditCardStatement);
            new[] { alert.Subject, alert.Sender, alert.DetectionReasons, alert.ExtractionWarnings }
                .Should().NotContain(v => v != null && (v.Contains("Rivera", StringComparison.Ordinal) || v.Contains("sign on", StringComparison.Ordinal)));
            (await reader.Statements.CountAsync()).Should().Be(2, "the payment reminder is not an alert");
        }
    }

    [Fact]
    public async Task Dismissed_alerts_are_not_recreated_and_alerts_already_answered_by_an_upload_start_dismissed()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);
        var gmail = new FakeGmailClient();
        gmail.Messages.Add(CibcAlert("a1", "5190", Now.AddDays(-40)));
        await DiscoverAsync(testDb, user.Id, gmail);

        var uploaded = await testDb.AddStatementAsync(user.Id, "CIBC", AccountType.CreditCard, "1111");
        var (arrange, _) = testDb.Context(user.Id);
        await using (arrange)
        {
            (await arrange.Statements.SingleAsync(s => s.SourceMessageId == "a1")).Status = StatementStatus.Dismissed;
            (await arrange.Statements.SingleAsync(s => s.Id == uploaded.Id)).PeriodEnd = DateOnly.FromDateTime(Now.AddDays(-10).UtcDateTime);
            await arrange.SaveChangesAsync();
        }

        // A later alert for the statement that was already uploaded, and one for another card.
        gmail.Messages.Add(CibcAlert("a2", "1111", Now.AddDays(-5)));
        gmail.Messages.Add(CibcAlert("a3", "2222", Now.AddDays(-5)));
        var rescan = await DiscoverAsync(testDb, user.Id, gmail);

        rescan.NewAlerts.Should().Be(1);
        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            var alerts = await reader.Statements.Where(s => s.Source == StatementSourceKind.Gmail).ToDictionaryAsync(s => s.SourceMessageId!, s => s.Status);
            alerts.Should().BeEquivalentTo(new Dictionary<string, StatementStatus>
            {
                ["a1"] = StatementStatus.Dismissed,
                ["a2"] = StatementStatus.Dismissed,
                ["a3"] = StatementStatus.AwaitingUpload,
            });
        }
    }
}

public sealed class DemoDataTests
{
    [Fact]
    public async Task Demo_users_get_a_year_of_categorized_data_and_expire_with_all_of_it()
    {
        await using var testDb = await TestDb.CreateAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));

        async Task<User> CreateDemoAsync()
        {
            var (db, userContext) = testDb.Context(null);
            await using (db)
            {
                var categorization = new CategorizationService(db, new FakeGeminiService(), clock, NullLogger<CategorizationService>.Instance);
                return await new DemoDataService(db, userContext, categorization, clock).CreateDemoUserAsync(CancellationToken.None);
            }
        }

        var old = await CreateDemoAsync();
        clock.Now = clock.Now.AddHours(30);
        var fresh = await CreateDemoAsync();
        var real = await testDb.AddUserAsync("Real", createdAt: clock.Now.AddDays(-400));

        var (reader, _) = testDb.Context(fresh.Id);
        await using (reader)
        {
            (await reader.Statements.CountAsync()).Should().Be(24);
            (await reader.Transactions.CountAsync(t => t.TransferPairId != null)).Should().BeGreaterThan(0);
            (await reader.Transactions.CountAsync(t => t.CategoryId == CategoryTaxonomy.Uncategorized && t.Type == TransactionType.Expense)).Should().Be(0);
        }

        var (cleanupDb, cleanupContext) = testDb.Context(null);
        await using (cleanupDb)
        {
            var categorization = new CategorizationService(cleanupDb, new FakeGeminiService(), clock, NullLogger<CategorizationService>.Instance);
            (await new DemoDataService(cleanupDb, cleanupContext, categorization, clock).DeleteExpiredDemoUsersAsync(TimeSpan.FromHours(24), CancellationToken.None))
                .Should().Be(1);
        }

        var (system, _) = testDb.SystemContext();
        await using (system)
        {
            (await system.Users.Select(u => u.Id).ToListAsync()).Should().BeEquivalentTo([fresh.Id, real.Id]);
            (await system.Transactions.AnyAsync(t => t.UserId == old.Id)).Should().BeFalse();
            (await system.Statements.AnyAsync(s => s.UserId == old.Id)).Should().BeFalse();
        }
    }
}

public sealed class PdfPigTextExtractorTests
{
    [Fact]
    public void Extracts_words_with_top_down_coordinates()
    {
        var document = new PdfPigTextExtractor().Extract(PdfStatementBuilder.SampleChequingStatement());

        var page = document.Pages.Single();
        var title = page.Words.First(w => w.Text == "Maple");
        var closing = page.Words.First(w => w.Text == "Closing");
        title.Top.Should().BeLessThan(closing.Top);
        page.Words.Should().Contain(w => w.Text == "3,100.00");
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("%PDF-1.7\nobj garbage endobj")]
    [InlineData("%PDF-")]
    public void Files_that_are_not_readable_pdfs_throw_unreadable(string content)
    {
        var act = () => new PdfPigTextExtractor().Extract(System.Text.Encoding.ASCII.GetBytes(content));

        act.Should().Throw<PdfUnreadableException>();
    }
}
