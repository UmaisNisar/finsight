using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinSight.Tests.TestHelpers;

namespace FinSight.Tests.Api;

public sealed class TransactionsApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    private static decimal Amount(JsonElement t) => t.GetProperty("amount").GetDecimal();

    private static string Str(JsonElement t, string property) => t.GetProperty(property).GetString()!;

    /// <summary>Demo data covers the twelve months before today, so tests use last month rather than a fixed date.</summary>
    private static string LastMonthRange()
    {
        var start = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        return $"from={start:yyyy-MM-dd}&to={start.AddMonths(1).AddDays(-1):yyyy-MM-dd}";
    }

    [Fact]
    public async Task Lists_filter_sort_and_paginate()
    {
        var client = await factory.CreateDemoClientAsync();

        var all = await (await client.GetAsync("/api/transactions?pageSize=10&page=2")).JsonAsync();
        all.GetProperty("items").GetArrayLength().Should().Be(10);
        all.GetProperty("page").GetInt32().Should().Be(2);
        var total = all.GetProperty("total").GetInt32();
        total.Should().BeGreaterThan(300);

        var dates = (await client.TransactionsAsync()).Select(t => Str(t, "date")).ToList();
        dates.Should().BeInDescendingOrder();

        var largest = await client.TransactionsAsync("sort=amount-desc");
        largest.Select(t => Math.Abs(Amount(t))).Should().BeInDescendingOrder();

        var income = await client.TransactionsAsync("type=income");
        income.Should().NotBeEmpty().And.OnlyContain(t => Str(t, "type") == "income" && Amount(t) > 0);

        var rent = await client.TransactionsAsync("categoryId=housing.rent");
        rent.Should().NotBeEmpty().And.OnlyContain(t => Amount(t) == -1850m);

        var food = await client.TransactionsAsync("groupId=food");
        food.Should().NotBeEmpty().And.OnlyContain(t => Str(t, "groupId") == "food");

        var netflix = await client.TransactionsAsync("search=netflix");
        netflix.Should().NotBeEmpty().And.OnlyContain(t => Str(t, "merchant") == "Netflix");

        var lastMonth = await client.TransactionsAsync(LastMonthRange());
        var prefix = LastMonthRange()[5..12];
        lastMonth.Should().NotBeEmpty().And.OnlyContain(t => Str(t, "date").StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_matches_statement_descriptions_as_well_as_merchant_names()
    {
        var client = await factory.CreateDemoClientAsync();

        var results = await client.TransactionsAsync("search=PRE-AUTHORIZED");

        results.Should().NotBeEmpty().And.OnlyContain(t => Str(t, "description").Contains("PRE-AUTHORIZED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Money_in_and_out_totals_exclude_transfers()
    {
        var client = await factory.CreateDemoClientAsync();

        var page = await (await client.GetAsync($"/api/transactions?pageSize=200&{LastMonthRange()}")).JsonAsync();
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var counted = items.Where(t => Str(t, "type") != "transfer" && !t.GetProperty("isExcluded").GetBoolean() && !t.GetProperty("isReversal").GetBoolean()).ToList();

        page.GetProperty("moneyIn").GetDecimal().Should().Be(counted.Where(t => Amount(t) > 0).Sum(Amount));
        page.GetProperty("moneyOut").GetDecimal().Should().Be(-counted.Where(t => Amount(t) < 0).Sum(Amount));
        items.Should().Contain(t => Str(t, "type") == "transfer");
    }

    [Fact]
    public async Task Invalid_edits_are_rejected_with_specific_codes()
    {
        var client = await factory.CreateDemoClientAsync();
        var rent = (await client.TransactionsAsync("categoryId=housing.rent"))[0];
        var id = Str(rent, "id");

        async Task<string> Code(object body)
        {
            var response = await client.PatchAsJsonAsync($"/api/transactions/{id}", body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            return await response.ErrorCodeAsync();
        }

        (await Code(new { categoryId = "made.up" })).Should().Be("invalid_category");
        (await Code(new { merchant = "   " })).Should().Be("invalid_merchant");
        (await Code(new { merchant = new string('x', 81) })).Should().Be("invalid_merchant");
        (await Code(new { type = "income" })).Should().Be("invalid_type");
        (await client.PatchAsJsonAsync($"/api/transactions/{Guid.NewGuid()}", new { isExcluded = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Marking_a_transfer_as_spending_moves_it_out_of_transfer_categories_and_into_expenses()
    {
        var client = await factory.CreateDemoClientAsync();
        var transfer = (await client.TransactionsAsync("search=TRANSFER TO SAVINGS&sort=date-desc"))[0];
        transfer.GetProperty("categoryId").GetString().Should().Be("financial.transfers");
        var month = Str(transfer, "date")[..7];
        var range = $"period=custom&from={month}-01&to={month}-{DateTime.DaysInMonth(int.Parse(month[..4]), int.Parse(month[5..]))}";
        var before = (await (await client.GetAsync($"/api/summary?{range}")).JsonAsync()).GetProperty("summary").GetProperty("expenses").GetDecimal();

        var updated = await (await client.PatchAsJsonAsync($"/api/transactions/{Str(transfer, "id")}", new { type = "expense" })).JsonAsync();

        Str(updated, "type").Should().Be("expense");
        Str(updated, "categoryId").Should().Be("other.uncategorized");
        updated.GetProperty("isEdited").GetBoolean().Should().BeTrue();
        var after = (await (await client.GetAsync($"/api/summary?{range}")).JsonAsync()).GetProperty("summary").GetProperty("expenses").GetDecimal();
        after.Should().Be(before + 500m);
    }

    [Fact]
    public async Task Marking_spending_as_a_transfer_uses_the_transfers_category_and_reset_restores_it()
    {
        var client = await factory.CreateDemoClientAsync();
        var etransfer = (await client.TransactionsAsync("search=E-TFR"))[0];
        var id = Str(etransfer, "id");
        var originalCategory = Str(etransfer, "categoryId");

        var asTransfer = await (await client.PatchAsJsonAsync($"/api/transactions/{id}", new { type = "transfer" })).JsonAsync();
        Str(asTransfer, "type").Should().Be("transfer");
        Str(asTransfer, "categoryId").Should().Be("financial.transfers");

        var reset = await (await client.PatchAsJsonAsync($"/api/transactions/{id}", new { resetOverrides = true })).JsonAsync();
        Str(reset, "type").Should().Be("expense");
        Str(reset, "categoryId").Should().Be(originalCategory);
        reset.GetProperty("isEdited").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_a_category_that_matches_the_pipeline_type_is_not_recorded_as_a_type_override()
    {
        var client = await factory.CreateDemoClientAsync();
        var coffee = (await client.TransactionsAsync("categoryId=food.coffee"))[0];

        var updated = await (await client.PatchAsJsonAsync($"/api/transactions/{Str(coffee, "id")}", new { categoryId = "food.restaurants" })).JsonAsync();

        Str(updated, "type").Should().Be("expense");
        Str(updated, "categoryId").Should().Be("food.restaurants");
        Str(updated, "categorySource").Should().Be("user");
    }

    [Fact]
    public async Task A_merchant_rule_recategorizes_past_transactions_and_matches_future_imports_even_after_a_rename()
    {
        var client = await factory.CreateDemoClientAsync();
        await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 6, 900m, [(4, "SQ *QUILL AND INK STUDIO", -42m), (18, "SQ *QUILL AND INK STUDIO", -18m)]));
        var existing = await client.TransactionsAsync("search=Quill");
        existing.Should().HaveCount(2);

        // Renamed first, then later made into a rule for the merchant.
        var id = Str(existing[0], "id");
        (await client.PatchAsJsonAsync($"/api/transactions/{id}", new { merchant = "Stationery shop" })).EnsureSuccessStatusCode();
        var updated = await (await client.PatchAsJsonAsync($"/api/transactions/{id}", new { categoryId = "personal.education", applyToMerchant = true })).JsonAsync();
        Str(updated, "merchant").Should().Be("Stationery shop");

        // The sibling transaction follows the rule without being individually edited.
        var sibling = (await client.TransactionsAsync("search=Quill")).Single(t => Str(t, "id") != Str(existing[0], "id"));
        Str(sibling, "categoryId").Should().Be("personal.education");
        sibling.GetProperty("isEdited").GetBoolean().Should().BeFalse();

        // A new statement from the same merchant is categorized by the rule on import.
        await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 7, 840m, [(9, "SQ *QUILL AND INK STUDIO", -27.50m)]));
        var imported = (await client.TransactionsAsync("from=2026-07-01&to=2026-07-31&search=QUILL")).Single();
        Str(imported, "categoryId").Should().Be("personal.education");
        Str(imported, "categorySource").Should().Be("user");
    }
}

public sealed class StatementsApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task Deleting_a_statement_removes_its_transactions_and_releases_matched_transfers()
    {
        var client = await factory.CreateDemoClientAsync();
        var statements = (await (await client.GetAsync("/api/statements")).JsonAsync()).EnumerateArray().ToList();
        var bankPayment = (await client.TransactionsAsync("search=PAYMENT HARBOUR CARD&sort=date-desc"))[0];
        bankPayment.GetProperty("type").GetString().Should().Be("transfer");
        var month = bankPayment.GetProperty("date").GetString()![..7];
        var card = statements.Single(s => s.GetProperty("institution").GetString() == "Harbour Card" && s.GetProperty("periodEnd").GetString()!.StartsWith(month, StringComparison.Ordinal));
        var cardId = card.GetProperty("id").GetString();

        (await client.DeleteAsync($"/api/statements/{cardId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync($"/api/statements/{cardId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.TransactionsAsync($"statementId={cardId}")).Should().BeEmpty();
        var released = (await client.TransactionsAsync("search=PAYMENT HARBOUR CARD&sort=date-desc"))[0];
        released.GetProperty("id").GetString().Should().Be(bankPayment.GetProperty("id").GetString());
        released.GetProperty("type").GetString().Should().NotBe("transfer");
        (await client.DeleteAsync($"/api/statements/{cardId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Processing_requests_are_validated()
    {
        var client = await factory.CreateDemoClientAsync();
        var demoStatement = (await (await client.GetAsync("/api/statements")).JsonAsync())[0].GetProperty("id").GetString();

        var none = await client.PostAsJsonAsync("/api/statements/process", new { statementIds = new[] { Guid.NewGuid() } });
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await none.ErrorCodeAsync()).Should().Be("no_statements");

        var demo = await client.PostAsJsonAsync("/api/statements/process", new { statementIds = new[] { demoStatement } });
        demo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await demo.ErrorCodeAsync()).Should().Be("demo_mode");

        var sync = await client.PostAsync("/api/statements/sync", null);
        sync.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await sync.ErrorCodeAsync()).Should().Be("demo_mode");

        var missingBody = await client.PostAsJsonAsync("/api/statements/process", new { });
        missingBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reprocessing_an_uploaded_statement_requires_the_file_again()
    {
        var client = await factory.CreateDemoClientAsync();
        var statementId = await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 5, 700m, [(2, "NETFLIX.COM", -20.99m)]));

        var response = await client.PostAsync($"/api/statements/{statementId}/process", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("upload_required");
        var detail = await (await client.GetAsync($"/api/statements/{statementId}")).JsonAsync();
        detail.GetProperty("statement").GetProperty("reprocessNeedsUpload").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Re_uploading_to_a_statement_keeps_the_users_edits()
    {
        var client = await factory.CreateDemoClientAsync();
        var pdf = PdfStatementBuilder.Chequing(2026, 4, 1200m, [(3, "NETFLIX.COM", -20.99m), (9, "LOBLAWS #221", -84.10m)]);
        var statementId = await client.ImportAsync(pdf);
        var netflix = (await client.TransactionsAsync($"statementId={statementId}&search=Netflix")).Single();
        await client.PatchAsJsonAsync($"/api/transactions/{netflix.GetProperty("id").GetString()}", new { isExcluded = true, categoryId = "personal.education" });

        await client.ImportAsync(pdf, statementId);

        var after = await client.TransactionsAsync($"statementId={statementId}");
        after.Should().HaveCount(2);
        var edited = after.Single(t => t.GetProperty("merchant").GetString() == "Netflix");
        edited.GetProperty("isExcluded").GetBoolean().Should().BeTrue();
        edited.GetProperty("categoryId").GetString().Should().Be("personal.education");
    }

    [Fact]
    public async Task Identical_uploads_at_the_same_time_create_one_statement()
    {
        var client = await factory.CreateDemoClientAsync();
        var pdf = PdfStatementBuilder.Chequing(2026, 3, 1500m, [(5, "SHELL C04512", -61.10m)], note: Guid.NewGuid().ToString());

        var uploads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.UploadAsync(pdf)));

        uploads.Select(u => u.GetProperty("statementId").GetString()).Distinct().Should().ContainSingle();
        foreach (var upload in uploads)
        {
            await client.WaitForJobAsync(upload.GetProperty("jobId").GetString()!);
        }

        (await client.TransactionsAsync($"statementId={uploads[0].GetProperty("statementId").GetString()}")).Should().ContainSingle();
    }

    [Fact]
    public async Task Uploads_are_validated()
    {
        var client = await factory.CreateDemoClientAsync();
        var other = await factory.CreateDemoClientAsync();
        var othersStatement = (await (await other.GetAsync("/api/statements")).JsonAsync())[0].GetProperty("id").GetGuid();

        using (var noFile = new MultipartFormDataContent { { new StringContent("hello"), "note" } })
        {
            var response = await client.PostAsync("/api/uploads/statements", noFile);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.ErrorCodeAsync()).Should().Be("file_missing");
        }

        var act = () => client.UploadAsync(PdfStatementBuilder.SampleChequingStatement(), statementId: othersStatement);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("404");
    }

    [Fact]
    public async Task A_scanned_statement_fails_with_a_readable_message()
    {
        var client = await factory.CreateDemoClientAsync();

        var started = await client.UploadAsync(PdfStatementBuilder.Blank());
        var job = await client.WaitForJobAsync(started.GetProperty("jobId").GetString()!);

        job.GetProperty("status").GetString().Should().Be("succeeded");
        job.GetProperty("steps")[0].GetProperty("status").GetString().Should().Be("failed");
        var statement = (await (await client.GetAsync($"/api/statements/{started.GetProperty("statementId").GetString()}")).JsonAsync()).GetProperty("statement");
        statement.GetProperty("status").GetString().Should().Be("failed");
        statement.GetProperty("failureCode").GetString().Should().Be("pdf_no_text");
        statement.GetProperty("failureMessage").GetString().Should().Contain("scanned image");
    }

    [Fact]
    public async Task Jobs_are_private_to_their_owner()
    {
        var alice = await factory.CreateDemoClientAsync();
        var bob = await factory.CreateDemoClientAsync();
        var started = await alice.UploadAsync(PdfStatementBuilder.Chequing(2026, 2, 400m, [(7, "NETFLIX.COM", -20.99m)]));
        var jobId = started.GetProperty("jobId").GetString();
        await alice.WaitForJobAsync(jobId!);

        (await bob.GetAsync($"/api/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync("/api/jobs/active")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Gmail_is_reported_as_not_connected_for_demo_users()
    {
        var client = await factory.CreateDemoClientAsync();

        var gmail = await (await client.GetAsync("/api/gmail")).JsonAsync();

        gmail.GetProperty("connected").GetBoolean().Should().BeFalse();
        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        session.GetProperty("capabilities").GetProperty("gmail").GetBoolean().Should().BeFalse();
    }
}

public sealed class AccountApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task Settings_round_trip_and_reject_unsupported_values()
    {
        var client = await factory.CreateDemoClientAsync();
        var defaults = await (await client.GetAsync("/api/settings")).JsonAsync();
        defaults.GetProperty("currency").GetString().Should().Be("CAD");

        var saved = await (await client.PutAsJsonAsync("/api/settings", new
        {
            currency = "eur",
            dateFormat = "dd/MM/yyyy",
            theme = "dark",
            aiCategorizationEnabled = false,
            aiInsightsEnabled = true,
            notificationsEnabled = true,
        })).JsonAsync();
        saved.GetProperty("currency").GetString().Should().Be("EUR");
        (await (await client.GetAsync("/api/settings")).JsonAsync()).GetProperty("theme").GetString().Should().Be("dark");
        (await (await client.GetAsync("/api/summary?period=last-month")).JsonAsync()).GetProperty("currency").GetString().Should().Be("EUR");

        var badCurrency = await client.PutAsJsonAsync("/api/settings", new { currency = "BTC", dateFormat = "dd/MM/yyyy", theme = "dark" });
        (await badCurrency.ErrorCodeAsync()).Should().Be("invalid_currency");
        var badFormat = await client.PutAsJsonAsync("/api/settings", new { currency = "USD", dateFormat = "yyyy", theme = "dark" });
        (await badFormat.ErrorCodeAsync()).Should().Be("invalid_date_format");
    }

    [Fact]
    public async Task Custom_categories_can_be_created_listed_and_used()
    {
        var client = await factory.CreateDemoClientAsync();

        var created = await (await client.PostAsJsonAsync("/api/categories", new { name = "Dog care", groupId = "personal" })).JsonAsync();
        var id = created.GetProperty("id").GetString();
        id.Should().Be("custom.personal.dog-care");

        var groups = await (await client.GetAsync("/api/categories")).JsonAsync();
        groups.EnumerateArray().Single(g => g.GetProperty("id").GetString() == "personal")
            .GetProperty("categories").EnumerateArray().Should().Contain(c => c.GetProperty("id").GetString() == id && c.GetProperty("isCustom").GetBoolean());

        var coffee = (await client.TransactionsAsync("categoryId=food.coffee"))[0];
        var updated = await (await client.PatchAsJsonAsync($"/api/transactions/{coffee.GetProperty("id").GetString()}", new { categoryId = id })).JsonAsync();
        updated.GetProperty("categoryName").GetString().Should().Be("Dog care");
        updated.GetProperty("groupId").GetString().Should().Be("personal");

        var duplicate = await client.PostAsJsonAsync("/api/categories", new { name = "dog care", groupId = "personal" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("x", "personal")]
    [InlineData("Side hustle", "income")]
    [InlineData("Brokerage", "financial")]
    [InlineData("Snacks", "nope")]
    public async Task Custom_categories_need_a_name_and_a_spending_group(string name, string groupId)
    {
        var client = await factory.CreateDemoClientAsync();

        var response = await client.PostAsJsonAsync("/api/categories", new { name, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorCodeAsync()).Should().Be("invalid_category");
    }

    [Fact]
    public async Task Categories_named_without_latin_letters_get_distinct_ids()
    {
        var client = await factory.CreateDemoClientAsync();

        var first = await client.PostAsJsonAsync("/api/categories", new { name = "日本食", groupId = "food" });
        var second = await client.PostAsJsonAsync("/api/categories", new { name = "Кофе", groupId = "food" });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.JsonAsync()).GetProperty("id").GetString().Should().NotBe((await second.JsonAsync()).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("Dog care", "dog-care")]
    [InlineData("  Kids -- school  ", "kids-school")]
    [InlineData("Café", "caf")]
    public void Category_slugs_are_readable(string name, string slug)
    {
        FinSight.Api.Controllers.AccountController.Slug(name.Trim()).Should().Be(slug);
    }

    [Fact]
    public async Task Deleting_transactions_resets_statements_and_clears_analyses()
    {
        var client = await factory.CreateDemoClientAsync();

        var result = await (await client.DeleteAsync("/api/data/transactions")).JsonAsync();

        result.GetProperty("deleted").GetInt32().Should().BeGreaterThan(300);
        (await (await client.GetAsync("/api/transactions")).JsonAsync()).GetProperty("total").GetInt32().Should().Be(0);
        // Demo statements have no source to download from again, so they are removed with their data.
        (await (await client.GetAsync("/api/statements")).JsonAsync()).GetArrayLength().Should().Be(0);
        (await (await client.GetAsync("/api/summary?period=last-month")).JsonAsync()).GetProperty("hasAnyData").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_statements_removes_everything_imported()
    {
        var client = await factory.CreateDemoClientAsync();

        (await (await client.DeleteAsync("/api/data/statements")).JsonAsync()).GetProperty("deleted").GetInt32().Should().Be(24);

        (await (await client.GetAsync("/api/transactions")).JsonAsync()).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Deleting_all_data_removes_rules_and_custom_categories_too_but_keeps_the_account()
    {
        var client = await factory.CreateDemoClientAsync();
        await client.PostAsJsonAsync("/api/categories", new { name = "Plants", groupId = "housing" });

        (await client.DeleteAsync("/api/data")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var groups = await (await client.GetAsync("/api/categories")).JsonAsync();
        groups.EnumerateArray().SelectMany(g => g.GetProperty("categories").EnumerateArray()).Should().NotContain(c => c.GetProperty("isCustom").GetBoolean());
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("authenticated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_the_account_signs_out_and_leaves_other_users_alone()
    {
        var client = await factory.CreateDemoClientAsync();
        var other = await factory.CreateDemoClientAsync();

        (await client.DeleteAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await other.TransactionsAsync()).Should().NotBeEmpty();
    }
}
