using System.Net;
using FinSight.Api.Hosting;
using FinSight.Tests.TestHelpers;

namespace FinSight.Tests.Api;

/// <summary>The API as deployed in a container: Production environment on PostgreSQL.</summary>
public sealed class PostgresApiFactory : FinSightApiFactory
{
    protected override bool UsePostgres => PostgresTestDatabase.IsAvailable;

    protected override string HostEnvironment => "Production";
}

/// <summary>
/// End-to-end requests against PostgreSQL. The full API suite can also run there (FINSIGHT_TEST_PROVIDER=Postgres); these cover
/// the paths most sensitive to the provider when only this category runs. Skipped unless FINSIGHT_TEST_POSTGRES is set.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    /// <summary>Production session cookies are Secure (__Host-), so the client must use HTTPS for them to be sent back.</summary>
    private async Task<HttpClient> DemoClientAsync()
    {
        var client = factory.CreateSessionClient(baseAddress: new Uri("https://localhost"));
        (await client.PostAsync("/api/auth/demo", null)).EnsureSuccessStatusCode();
        return client;
    }

    [PostgresFact]
    public async Task The_app_migrates_on_start_and_reports_ready()
    {
        var response = await factory.CreateSessionClient().GetAsync(HealthEndpoints.ReadyPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task Demo_data_is_stored_totalled_and_searched()
    {
        var client = await DemoClientAsync();

        var page = await (await client.GetAsync("/api/transactions?pageSize=50&sort=amount-desc")).JsonAsync();
        page.GetProperty("total").GetInt32().Should().BeGreaterThan(300);
        page.GetProperty("items").EnumerateArray().Select(t => Math.Abs(t.GetProperty("amount").GetDecimal())).Should().BeInDescendingOrder();

        (await client.TransactionsAsync("search=netflix")).Should().NotBeEmpty()
            .And.OnlyContain(t => t.GetProperty("merchant").GetString() == "Netflix");
        (await client.GetAsync("/api/summary")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/recurring")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/statements")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task Uploaded_statements_import_and_delete()
    {
        var client = await DemoClientAsync();
        (await client.DeleteAsync("/api/data")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var statementId = await client.ImportAsync(PdfStatementBuilder.Chequing(2026, 4, 1200m, [(3, "NETFLIX.COM", -20.99m), (9, "LOBLAWS #221", -84.10m)]));
        var imported = await client.TransactionsAsync();
        imported.Should().HaveCount(2);
        imported.Select(t => t.GetProperty("amount").GetDecimal()).Should().BeEquivalentTo([-20.99m, -84.10m]);

        (await client.DeleteAsync($"/api/statements/{statementId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.TransactionsAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Users_are_isolated_and_deleting_an_account_leaves_others_alone()
    {
        var client = await DemoClientAsync();
        var other = await DemoClientAsync();
        var otherIds = (await other.TransactionsAsync()).Select(t => t.GetProperty("id").GetString()).ToHashSet();

        (await client.TransactionsAsync()).Should().NotContain(t => otherIds.Contains(t.GetProperty("id").GetString()));

        (await client.DeleteAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await other.TransactionsAsync()).Should().NotBeEmpty();
    }
}
