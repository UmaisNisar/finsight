using FinSight.Core.Receipts;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Pipeline;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FinSight.Core.Abstractions;
using FinSight.Core.Domain;
using FinSight.Core.Statements;

namespace FinSight.Tests.Receipts;

public sealed class ReceiptRedactorTests
{
    [Fact]
    public void Removes_personal_details_but_keeps_items_and_prices()
    {
        const string body = """
            Hi Umais, your order is confirmed.
            Order 184733921
            Ship to: 123 Spadina Ave, Unit 4, Toronto ON M5V 2T6
            Questions? support@ikea.com or +1 (416) 555-0199
            Paid with Visa ending 8764 (4505 1234 1234 8764)

            BILLY bookcase, white    $129.00
            2x KALLAX insert          $80.00
            Total                    $696.01
            """;

        var redacted = ReceiptRedactor.Redact(body);

        redacted.Should().Contain("BILLY bookcase");
        redacted.Should().Contain("129.00").And.Contain("696.01");
        redacted.Should().NotContain("support@ikea.com");
        redacted.Should().NotContain("555-0199");
        redacted.Should().NotContain("4505");
        redacted.Should().NotContain("M5V 2T6");
        redacted.Should().NotContain("Spadina");
    }

    [Fact]
    public void Trims_to_the_character_cap()
    {
        var redacted = ReceiptRedactor.Redact(new string('a', 10_000), maxChars: 500);
        redacted.Length.Should().BeLessThanOrEqualTo(500);
    }
}

public sealed class ReceiptSendersTests
{
    [Theory]
    [InlineData("IKEA", "ikea.com")]
    [InlineData("Amazon", "amazon.ca")]
    [InlineData("Uber", "uber.com")]
    [InlineData("Uber Eats", "uber.com")]
    [InlineData("SkipTheDishes", "skipthedishes.com")]
    [InlineData("OpenAI", "openai.com")]
    [InlineData("Anthropic", "anthropic.com")]
    [InlineData("McDonald's", "mcdonalds.com")]
    public void Resolves_known_merchants(string merchant, string expectedDomain)
    {
        ReceiptSenders.For(merchant)!.Domains.Should().Contain(expectedDomain);
    }

    [Theory]
    [InlineData("Some Corner Store")]
    [InlineData("")]
    public void Returns_null_for_merchants_without_known_receipts(string merchant)
    {
        ReceiptSenders.For(merchant).Should().BeNull();
        ReceiptSenders.Knows(merchant).Should().BeFalse();
    }
}

public sealed class ReceiptExtractionValidatorTests
{
    [Fact]
    public void Drops_empty_names_and_absurd_amounts()
    {
        var raw = new RawReceiptExtraction
        {
            OrderNumber = "  ",
            Items =
            [
                new RawReceiptItem { Name = "BILLY bookcase", Quantity = 1, Amount = 129.0 },
                new RawReceiptItem { Name = "  ", Amount = 5 },
                new RawReceiptItem { Name = "Hallucinated", Amount = -3 },
            ],
            Total = 129.0,
        };

        var result = ReceiptExtractionValidator.Validate(raw);

        result.OrderNumber.Should().BeNull();
        result.Items.Should().HaveCount(2);
        result.Items[0].Name.Should().Be("BILLY bookcase");
        result.Items[0].Amount.Should().Be(129.00m);
        result.Items[1].Amount.Should().BeNull(); // negative price dropped
        result.Total.Should().Be(129.00m);
    }
}

public sealed class ReceiptMatchingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(int Considered, int Matched)> MatchAsync(TestDb testDb, Guid userId, FakeGmailClient gmail, FakeGeminiService gemini)
    {
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            var http = new StubHttpHandler().Json("""{ "access_token": "a", "expires_in": 3600 }""");
            var tokens = new GoogleTokenService(new HttpClient(http), db, testDb.Protector, new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new GoogleIntegrationOptions { ClientId = "c", ClientSecret = "s" }), NullLogger<GoogleTokenService>.Instance);
            var service = new ReceiptMatchingService(db, tokens, gmail, gemini, NullLogger<ReceiptMatchingService>.Instance);
            var transactions = await db.Transactions.ToListAsync();
            var result = await service.MatchAsync(userId, transactions, CancellationToken.None);
            return (result.Considered, result.Matched);
        }
    }

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

    private static EmailCandidate IkeaOrder(decimal amount, DateTimeOffset received) =>
        new("m1", "t1", "Your IKEA order is confirmed", "IKEA <order@ikea.com>",
            $"Thanks for your order. Order total ${amount:0.00}. Arriving soon.", received, []);

    [Fact]
    public async Task Matches_a_purchase_to_its_order_email_and_lists_what_was_bought()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);
        var statement = await testDb.AddStatementAsync(user.Id, "CIBC", AccountType.CreditCard, "8764");

        var (seed, _) = testDb.Context(user.Id);
        await using (seed)
        {
            seed.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 9, 8), "IKEA.CA ONLINE", -696.01m, "IKEA", "housing.home"));
            await seed.SaveChangesAsync();
        }

        var gmail = new FakeGmailClient();
        gmail.Messages.Add(IkeaOrder(696.01m, new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)));
        gmail.Bodies["m1"] = "Order 184733921. BILLY bookcase white $129.00. Delivery $49. Total $696.01.";

        var gemini = new FakeGeminiService
        {
            ExtractReceipt = (_, _) => new ReceiptExtraction("184733921",
                [new ReceiptItem("BILLY bookcase, white", 1, 129.00m)], 696.01m),
        };

        var (considered, matched) = await MatchAsync(testDb, user.Id, gmail, gemini);

        considered.Should().Be(1);
        matched.Should().Be(1);

        var (verify, _) = testDb.Context(user.Id);
        await using (verify)
        {
            var receipt = await verify.TransactionReceipts.SingleAsync();
            receipt.Source.Should().Be(ReceiptSource.Ai);
            receipt.OrderNumber.Should().Be("184733921");
            receipt.Subject.Should().Be("Your IKEA order is confirmed");
            receipt.ItemsJson.Should().Contain("BILLY bookcase");
        }
    }

    [Fact]
    public async Task Falls_back_to_a_link_only_match_when_the_ai_is_unavailable()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);
        var statement = await testDb.AddStatementAsync(user.Id, "CIBC", AccountType.CreditCard, "8764");

        var (seed, _) = testDb.Context(user.Id);
        await using (seed)
        {
            seed.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 9, 8), "IKEA.CA ONLINE", -696.01m, "IKEA", "housing.home"));
            await seed.SaveChangesAsync();
        }

        var gmail = new FakeGmailClient();
        gmail.Messages.Add(IkeaOrder(696.01m, new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)));
        gmail.Bodies["m1"] = "Order details inside.";

        var gemini = new FakeGeminiService { ExtractReceipt = (_, _) => throw new AiUnavailableException(AiFailure.LimitReached) };

        var (_, matched) = await MatchAsync(testDb, user.Id, gmail, gemini);

        matched.Should().Be(1);
        var (verify, _) = testDb.Context(user.Id);
        await using (verify)
        {
            var receipt = await verify.TransactionReceipts.SingleAsync();
            receipt.Source.Should().Be(ReceiptSource.LinkOnly);
            receipt.ItemsJson.Should().Be("[]");
            receipt.Subject.Should().Be("Your IKEA order is confirmed");
        }
    }

    [Fact]
    public async Task Ignores_transactions_with_no_known_receipt_sender()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await ConnectGmailAsync(testDb, user.Id);
        var statement = await testDb.AddStatementAsync(user.Id);

        var (seed, _) = testDb.Context(user.Id);
        await using (seed)
        {
            seed.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 9, 8), "CORNER STORE", -12.00m, "Corner Store"));
            await seed.SaveChangesAsync();
        }

        var (considered, matched) = await MatchAsync(testDb, user.Id, new FakeGmailClient(), new FakeGeminiService());

        considered.Should().Be(0);
        matched.Should().Be(0);
    }
}
