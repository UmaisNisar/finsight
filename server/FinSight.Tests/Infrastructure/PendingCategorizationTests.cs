using FinSight.Core.Abstractions;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using FinSight.Tests.Api;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinSight.Tests.Infrastructure;

/// <summary>Merchants left uncategorized while AI was unavailable get another go once it's back.</summary>
public sealed class PendingCategorizationTests
{
    private static readonly DateOnly Day = new(2026, 8, 12);

    private static CategorizationService Service(FinSightDbContext db, IGeminiService gemini) =>
        new(db, gemini, TimeProvider.System, NullLogger<CategorizationService>.Instance);

    private static List<MerchantCategorization> Shopping(IReadOnlyList<MerchantCategorizationRequest> requests) =>
        requests.Select(r => new MerchantCategorization(r.MerchantKey, r.Direction, "shopping.general", TransactionType.Expense, null, 0.9, "Online store")).ToList();

    [Fact]
    public async Task Pending_merchants_are_categorized_when_ai_returns_and_user_choices_are_never_overwritten()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var january = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService { Categorize = _ => throw new AiUnavailableException(AiFailure.RateLimited) };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var edited = TestDb.NewTransaction(january, Day, "ASDF MARKET", -9m, "asdf");
            edited.UserCategoryId = "health.pharmacy";
            var retyped = TestDb.NewTransaction(january, Day, "QWERTY CO", -14m, "qwerty");
            retyped.UserType = TransactionType.Transfer;
            db.MerchantRules.Add(new MerchantRule { UserId = user.Id, MerchantKey = "corner", CategoryId = "food.groceries", Source = CategorySource.User, Confidence = 1 });
            db.Transactions.AddRange(TestDb.NewTransaction(january, Day, "ZXQ HOLDINGS", -45m, "zxq"), TestDb.NewTransaction(january, Day, "CORNER", -30m, "corner"), edited, retyped);
            await db.SaveChangesAsync();

            var failed = await Service(db, gemini).CategorizeWithAiAsync([january.Id], CancellationToken.None);
            failed.Failure.Should().Be(AiFailure.RateLimited);
            (await db.Transactions.CountAsync(t => t.CategoryId == CategoryTaxonomy.Uncategorized)).Should().Be(4);

            gemini.Categorize = Shopping;
            var retried = await Service(db, gemini).RetryPendingWithAiAsync(new HashSet<string>(), CancellationToken.None);

            retried.Outcome.Failure.Should().BeNull();
            gemini.CategorizationCalls[^1].Select(r => r.MerchantKey).Should().BeEquivalentTo(["zxq", "corner"], "edited transactions are settled and never sent");
            var zxq = await db.Transactions.SingleAsync(t => t.MerchantKey == "zxq");
            zxq.CategoryId.Should().Be("shopping.general");
            zxq.CategorySource.Should().Be(CategorySource.Ai);
            (await db.Transactions.SingleAsync(t => t.MerchantKey == "asdf")).UserCategoryId.Should().Be("health.pharmacy");
            (await db.Transactions.SingleAsync(t => t.MerchantKey == "asdf")).CategoryId.Should().Be(CategoryTaxonomy.Uncategorized);
            (await db.Transactions.SingleAsync(t => t.MerchantKey == "qwerty")).CategoryId.Should().Be(CategoryTaxonomy.Uncategorized);
            (await db.Transactions.SingleAsync(t => t.MerchantKey == "corner")).CategoryId.Should().Be(CategoryTaxonomy.Uncategorized, "the user's own rule for the merchant wins over AI");
            (await db.MerchantRules.SingleAsync(r => r.MerchantKey == "corner")).CategoryId.Should().Be("food.groceries");

            var again = await Service(db, gemini).RetryPendingWithAiAsync(new HashSet<string>(), CancellationToken.None);
            again.Outcome.MerchantsSent.Should().Be(1, "zxq is settled now; only the merchant the user's rule kept back is still waiting");
        }
    }

    [Fact]
    public async Task Merchants_ai_could_not_place_are_reported_so_they_can_be_skipped_next_time()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService
        {
            Categorize = requests => Shopping(requests.Where(r => r.MerchantKey != "mystery").ToList()),
        };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.AddRange(TestDb.NewTransaction(statement, Day, "MYSTERY 42", -60m, "mystery"), TestDb.NewTransaction(statement, Day, "ZXQ", -5m, "zxq"));
            await db.SaveChangesAsync();

            var first = await Service(db, gemini).RetryPendingWithAiAsync(new HashSet<string>(), CancellationToken.None);
            first.Declined.Should().Equal("mystery");

            var calls = gemini.CategorizationCalls.Count;
            var second = await Service(db, gemini).RetryPendingWithAiAsync(first.Declined.ToHashSet(), CancellationToken.None);

            second.Outcome.MerchantsSent.Should().Be(0);
            gemini.CategorizationCalls.Should().HaveCount(calls, "nothing is left to ask about");
        }
    }

    [Fact]
    public async Task Nothing_is_retried_without_a_key()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var gemini = new FakeGeminiService { IsConfigured = false };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, Day, "ZXQ", -5m, "zxq"));
            await db.SaveChangesAsync();

            (await Service(db, gemini).RetryPendingWithAiAsync(new HashSet<string>(), CancellationToken.None)).Outcome.Failure.Should().Be(AiFailure.NotConfigured);
            gemini.CategorizationCalls.Should().BeEmpty();
        }
    }
}

/// <summary>The background retry, end to end: a user imports without a key, adds one, and their merchants get categorized.</summary>
public sealed class PendingCategorizationApiTests(AiKeyApiFactory factory) : IClassFixture<AiKeyApiFactory>
{
    [Fact]
    public async Task Adding_a_key_lets_the_retry_categorize_what_was_imported_without_one()
    {
        var client = await factory.CreateGoogleUserClientAsync("pending@example.com");
        var userId = await ApiTestData.UserIdAsync(client);
        var job = await ApiTestData.ImportUnknownMerchantAsync(client);
        ApiTestData.StepDetail(job, "categorize").Should().Be("Categorized with rules (AI not configured)");
        ApiTestData.StepDetail(job, "insights").Should().EndWith("written without AI");

        await ApiTestData.SaveKeyAsync(factory, client, ApiTestData.NewKey());
        factory.GeminiHttp.Json(GeminiChainTests.Candidate("""{"results":[{"ref":"M1","categoryId":"shopping.general","merchant":"Zxq Holdings","confidence":0.9,"reason":"Store"}]}"""));

        var retry = factory.Services.GetRequiredService<PendingCategorizationRetry>();
        var result = await retry.RetryUserAsync(userId, CancellationToken.None);

        result!.Outcome.MerchantsCategorized.Should().Be(1);
        var charge = (await client.TransactionsAsync()).Single(t => t.GetProperty("amount").GetDecimal() == -45m);
        charge.GetProperty("categoryId").GetString().Should().Be("shopping.general");
        charge.GetProperty("categorySource").GetString().Should().Be("ai");
    }

    [Fact]
    public async Task The_sweep_skips_demo_users_and_users_who_turned_ai_categorization_off()
    {
        var demo = await factory.CreateDemoClientAsync();
        var demoId = await ApiTestData.UserIdAsync(demo);
        var retry = factory.Services.GetRequiredService<PendingCategorizationRetry>();
        var calls = factory.GeminiHttp.Requests.Count;

        (await retry.RetryUserAsync(demoId, CancellationToken.None)).Should().BeNull();
        await retry.SweepAsync(CancellationToken.None);

        factory.GeminiHttp.Requests.Count.Should().Be(calls, "no user here has both a key and pending merchants");
    }
}
