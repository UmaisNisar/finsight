using System.Globalization;
using FinSight.Core.Domain;
using FinSight.Core.Normalization;
using FinSight.Core.Parsing;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Infrastructure.Demo;

/// <summary>
/// Creates a demo user with twelve months of entirely synthetic data: a fictional bank and card,
/// fictional employer and landlord, and well-known consumer merchants. No real financial data is used.
/// The transactions go through the same normalization, categorization and transfer matching as real imports.
/// </summary>
public sealed class DemoDataService(FinSightDbContext db, UserContext userContext, CategorizationService categorization, TimeProvider time)
{
    private const string BankName = "Northwind Bank";
    private const string CardName = "Harbour Card";

    public async Task<User> CreateDemoUserAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var user = new User
        {
            Email = "demo@finsight.local",
            DisplayName = "Alex",
            IsDemo = true,
            CreatedAt = now,
        };

        userContext.SetUser(user.Id);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-12);
        var random = new Random(20260901);

        var statements = new List<(Statement Statement, List<ParsedTransaction> Rows)>();
        decimal cardBalance = 0;

        for (var m = 0; m < 12; m++)
        {
            var month = firstMonth.AddMonths(m);
            var days = DateTime.DaysInMonth(month.Year, month.Month);
            var bank = new List<ParsedTransaction>();
            var card = new List<ParsedTransaction>();

            void Bank(int day, string description, decimal amount) =>
                bank.Add(new ParsedTransaction(Day(month, day), null, description, amount, null, 0.97, 1));
            void Card(int day, string description, decimal amount) =>
                card.Add(new ParsedTransaction(Day(month, day), null, description, amount, null, 0.97, 1));
            decimal Between(double min, double max) => decimal.Round((decimal)(min + (random.NextDouble() * (max - min))), 2);

            // Income: biweekly salary, occasional freelance work, interest.
            for (var payday = firstMonth.AddDays(4); payday <= month.AddDays(days - 1); payday = payday.AddDays(14))
            {
                if (payday >= month)
                {
                    Bank(payday.Day, "PAYROLL DEPOSIT LUMEN LABS INC", 2650.00m);
                }
            }

            if (m % 3 != 1)
            {
                Bank(20, "STRIPE PAYOUT NORTHSTAR STUDIO", Between(550, 1200));
            }

            Bank(days, "INTEREST PAID", Between(1.2, 3.8));

            // Housing and bills.
            Bank(1, "PRE-AUTHORIZED DEBIT MAPLE RESIDENTIAL RENT", -1850.00m);
            Bank(6, "TORONTO HYDRO ELECTRIC", -Between(58, 112));
            Bank(9, "ENBRIDGE GAS DISTRIBUTION", -Between(38, month.Month is 12 or 1 or 2 ? 140 : 70));
            Bank(14, "ROGERS WIRELESS INTERNET", -95.00m);
            Bank(16, "SONNET INSURANCE TENANT", -28.40m);

            // Money moving between the user's own accounts: never spending.
            Bank(2, "INTERNET TRANSFER TO SAVINGS 0042", -500.00m);
            Bank(3, "WEALTHSIMPLE INVESTMENTS TFSA", -300.00m);
            if (cardBalance > 0)
            {
                Bank(18, "ONLINE BANKING PAYMENT HARBOUR CARD", -cardBalance);
                Card(19, "PAYMENT - THANK YOU", cardBalance);
            }

            if (m % 4 == 2)
            {
                Bank(11, "MONTHLY ACCOUNT FEE", -4.95m);
            }

            if (random.NextDouble() < 0.5)
            {
                Bank(random.Next(5, 25), "SEND E-TFR JORDAN", -Between(20, 90));
            }

            if (random.NextDouble() < 0.35)
            {
                Bank(random.Next(5, 25), "ATM WITHDRAWAL", -60.00m);
            }

            // Subscriptions and memberships.
            Card(3, "SPOTIFY P2A3B4C5", -11.99m);
            Card(5, "GOODLIFE FITNESS CLUBS", -64.99m);
            Card(12, "NETFLIX.COM", -20.99m);
            Card(22, "APPLE.COM/BILL", -3.99m);
            if (m >= 7)
            {
                Card(15, "DISNEY PLUS", -14.99m);
            }

            // Everyday spending.
            var grocers = new[] { "LOBLAWS #1021", "NO FRILLS 3345", "FARM BOY #12", "COSTCO WHOLESALE #535" };
            for (var i = 0; i < random.Next(6, 9); i++)
            {
                Card(random.Next(1, days + 1), grocers[random.Next(grocers.Length)], -Between(38, 150));
            }

            var restaurants = new[] { "PAI NORTHERN THAI KITCHEN", "BLUE DOOR CAFE BISTRO", "PIZZERIA LIBRETTO", "RAMEN ISSHIN", "BURRITO BOYZ", "THE ELM TREE GRILL" };
            var restaurantVisits = m >= 9 ? random.Next(7, 10) : random.Next(4, 7);
            for (var i = 0; i < restaurantVisits; i++)
            {
                Card(random.Next(1, days + 1), restaurants[random.Next(restaurants.Length)], -Between(28, 95));
            }

            for (var i = 0; i < random.Next(3, 6); i++)
            {
                Card(random.Next(1, days + 1), "UBER* EATS", -Between(24, 52));
            }

            for (var i = 0; i < random.Next(8, 14); i++)
            {
                Card(random.Next(1, days + 1), random.NextDouble() < 0.6 ? "STARBUCKS 0421" : "TIM HORTONS #2215", -Between(3.5, 8.9));
            }

            Card(2, "PRESTO AUTOLOAD", -80.00m);
            Card(17, "PRESTO AUTOLOAD", -40.00m);
            for (var i = 0; i < random.Next(1, 4); i++)
            {
                Card(random.Next(1, days + 1), "UBER *TRIP", -Between(12, 34));
            }

            for (var i = 0; i < random.Next(2, 4); i++)
            {
                Card(random.Next(1, days + 1), "SHELL C04512", -Between(45, 72));
            }

            for (var i = 0; i < random.Next(2, 5); i++)
            {
                Card(random.Next(1, days + 1), "AMZN MKTP CA*" + random.Next(1000, 9999).ToString(CultureInfo.InvariantCulture), -Between(14, 110));
            }

            if (random.NextDouble() < 0.6)
            {
                Card(random.Next(1, days + 1), "SHOPPERS DRUG MART #1234", -Between(12, 48));
            }

            if (random.NextDouble() < 0.4)
            {
                Card(random.Next(1, days + 1), "CINEPLEX ENTERTAINMENT", -Between(24, 46));
            }

            if (random.NextDouble() < 0.3)
            {
                Card(random.Next(1, days + 1), "UNIQLO CANADA", -Between(40, 130));
            }

            // Moments worth an insight: a big purchase, a trip, a refund, a double charge.
            if (m == 10)
            {
                Card(8, "BEST BUY #0948", -1149.99m);
            }

            if (m == 4)
            {
                Card(10, "AIR CANADA", -612.40m);
                Card(24, "AIRBNB * HMQ8TZ", -486.00m);
            }

            if (m == 6)
            {
                Card(9, "AMZN MKTP CA*7731", -89.99m);
                Card(21, "AMZN MKTP CA*7731 REFUND", 89.99m);
            }

            if (m == 11)
            {
                Card(13, "CINEPLEX ENTERTAINMENT", -42.50m);
                Card(13, "CINEPLEX ENTERTAINMENT", -42.50m);
            }

            cardBalance = -card.Where(t => t.Amount < 0).Sum(t => t.Amount);

            var label = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            statements.Add((CreateStatement(user.Id, month, BankName, AccountType.Chequing, "4821", $"Northwind_Chequing_{label}.pdf", now), bank));
            statements.Add((CreateStatement(user.Id, month, CardName, AccountType.CreditCard, "4417", $"HarbourCard_{label}.pdf", now), card));
        }

        foreach (var (statement, rows) in statements)
        {
            var parsed = new ParsedStatement(
                new StatementMetadata(statement.Institution, statement.AccountType, statement.AccountMask, "CAD", statement.PeriodStart, statement.PeriodEnd, null, null),
                rows, [], 0.97, true, ParseFailure.None);

            var normalized = TransactionNormalizer.Normalize(parsed, TransactionNormalizer.AccountKey(statement.Institution, statement.AccountType, statement.AccountMask));
            var transactions = normalized.Select(n => new Transaction
            {
                UserId = user.Id,
                StatementId = statement.Id,
                Fingerprint = n.Fingerprint,
                Date = n.Date,
                Description = n.Description,
                Amount = n.Amount,
                Currency = "CAD",
                ExtractionConfidence = n.ExtractionConfidence,
                Merchant = n.Merchant.Display,
                MerchantKey = n.Merchant.Key,
                CategoryId = Core.Categories.CategoryTaxonomy.Uncategorized,
                IsReversal = n.IsReversal,
                CreatedAt = now,
            }).ToList();

            await categorization.ApplyRulesAsync(transactions, statement.AccountType, cancellationToken);
            statement.TransactionCount = transactions.Count;
            db.Statements.Add(statement);
            db.Transactions.AddRange(transactions);
        }

        await db.SaveChangesAsync(cancellationToken);
        await categorization.MatchTransfersAsync(firstMonth, today, cancellationToken);
        return user;
    }

    /// <summary>Deletes demo users older than the given age, with all their data.</summary>
    public async Task<int> DeleteExpiredDemoUsersAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        using var _ = userContext.BeginSystemScope();
        var cutoff = time.GetUtcNow() - maxAge;
        var expired = (await db.Users.Where(u => u.IsDemo).ToListAsync(cancellationToken)).Where(u => u.CreatedAt < cutoff).Select(u => u.Id).ToList();

        foreach (var id in expired)
        {
            await db.Transactions.Where(t => t.UserId == id).ExecuteDeleteAsync(cancellationToken);
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(cancellationToken);
        }

        return expired.Count;
    }

    private static Statement CreateStatement(Guid userId, DateOnly month, string institution, AccountType accountType, string mask, string filename, DateTimeOffset now)
    {
        var end = month.AddMonths(1).AddDays(-1);
        var sent = new DateTimeOffset(end.AddDays(3).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        return new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.Demo,
            SourceKey = $"demo:{accountType}:{month:yyyy-MM}",
            Subject = $"Your {month.ToString("MMMM", CultureInfo.InvariantCulture)} eStatement is ready",
            Sender = $"{institution} <statements@{institution.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}.example>",
            ReceivedAt = sent,
            Filename = filename,
            SizeBytes = 184_000,
            DocumentKind = accountType == AccountType.CreditCard ? DocumentKind.CreditCardStatement : DocumentKind.BankStatement,
            DetectionConfidence = 0.95,
            DetectionReasons = """["Synthetic demo statement"]""",
            Institution = institution,
            AccountType = accountType,
            AccountMask = mask,
            Currency = "CAD",
            PeriodStart = month,
            PeriodEnd = end,
            Status = StatementStatus.Processed,
            ExtractionConfidence = 0.97,
            ExtractionWarnings = "[]",
            CreatedAt = now,
            UpdatedAt = now,
            ProcessedAt = now,
        };
    }

    private static DateOnly Day(DateOnly month, int day) =>
        new(month.Year, month.Month, Math.Clamp(day, 1, DateTime.DaysInMonth(month.Year, month.Month)));
}
