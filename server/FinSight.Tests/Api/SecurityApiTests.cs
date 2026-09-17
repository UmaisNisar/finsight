using System.Net;
using System.Net.Http.Json;
using FinSight.Api.Auth;
using FinSight.Api.Controllers;
using FinSight.Tests.TestHelpers;
using Microsoft.Data.Sqlite;

namespace FinSight.Tests.Api;

public sealed class SecurityApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Theory]
    [InlineData("/api/auth/session")]
    [InlineData("/")]
    public async Task Every_response_carries_security_headers(string path)
    {
        var response = await factory.CreateSessionClient().GetAsync(path);

        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task The_web_app_is_served_with_a_strict_content_security_policy()
    {
        var response = await factory.CreateSessionClient().GetAsync("/");

        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        csp.Should().Contain("default-src 'self'").And.Contain("frame-ancestors 'none'").And.Contain("script-src 'self'");
        csp.Should().NotContain("unsafe-eval");
    }

    [Theory]
    [InlineData("DELETE", "/api/data")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("POST", "/api/statements/sync")]
    [InlineData("PATCH", "/api/transactions/00000000-0000-0000-0000-000000000001")]
    public async Task Signed_in_state_changes_without_the_csrf_header_are_blocked(string method, string path)
    {
        var client = await factory.CreateDemoClientAsync();
        client.DefaultRequestHeaders.Remove("X-FinSight-Request");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.ErrorCodeAsync()).Should().Be("csrf_rejected");
        (await client.TransactionsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_csrf_header_with_the_wrong_value_is_blocked()
    {
        var client = await factory.CreateDemoClientAsync();
        client.DefaultRequestHeaders.Remove("X-FinSight-Request");
        client.DefaultRequestHeaders.Add("X-FinSight-Request", "yes");

        var response = await client.DeleteAsync("/api/data");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_api_calls_get_401_not_a_login_redirect()
    {
        var response = await factory.CreateSessionClient().GetAsync("/api/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_api_routes_return_a_json_404_instead_of_the_web_app()
    {
        var response = await factory.CreateSessionClient().GetAsync("/api/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.ErrorCodeAsync()).Should().Be("not_found");
    }

    [Fact]
    public async Task Invalid_request_bodies_return_a_stable_error_without_details()
    {
        var client = await factory.CreateDemoClientAsync();

        var response = await client.PutAsync("/api/settings", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        (await response.ErrorCodeAsync()).Should().Be("invalid_request");
        body.Should().NotContain("Exception").And.NotContain("   at ");
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var client = await factory.CreateDemoClientAsync();

        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_session_for_a_deleted_account_stops_working_immediately()
    {
        var client = await factory.CreateDemoClientAsync();
        var stolenCookie = factory.CreateSessionClient();
        var cookie = (await client.GetAsync("/api/auth/session")).RequestMessage!.Headers.GetValues("Cookie").Single();
        stolenCookie.DefaultRequestHeaders.Add("Cookie", cookie);
        (await stolenCookie.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await stolenCookie.GetAsync("/api/settings");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("session_ended");
    }

    [Fact]
    public async Task Transaction_descriptions_are_encrypted_in_the_database_file()
    {
        await factory.CreateDemoClientAsync();

        await using var connection = new SqliteConnection($"Data Source={factory.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Description FROM Transactions LIMIT 50";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        values.Should().NotBeEmpty().And.OnlyContain(v => v.StartsWith("enc:", StringComparison.Ordinal));
        values.Should().NotContain(v => v.Contains("PAYROLL", StringComparison.Ordinal) || v.Contains("NETFLIX", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/statements", "/statements")]
    [InlineData("/transactions?period=last-3-months", "/transactions?period=last-3-months")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("/\n/evil.example", "/")]
    [InlineData(" /evil", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void Return_urls_are_limited_to_this_site(string? input, string expected)
    {
        ReturnUrls.Safe(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("last-month", true)]
    [InlineData("LAST-3-MONTHS", true)]
    [InlineData("next-month", false)]
    [InlineData("custom", false)]
    public void Period_presets_are_validated(string period, bool valid)
    {
        new PeriodQuery { Period = period }.TryResolve(new DateOnly(2026, 9, 16), out _, out _).Should().Be(valid);
    }

    [Fact]
    public void Custom_periods_need_ordered_dates_at_most_three_years_apart()
    {
        var today = new DateOnly(2026, 9, 16);
        bool Valid(string from, string to) =>
            new PeriodQuery { Period = "custom", From = DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), To = DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture) }
                .TryResolve(today, out _, out _);

        Valid("2026-01-01", "2026-03-31").Should().BeTrue();
        Valid("2026-03-31", "2026-01-01").Should().BeFalse();
        Valid("2020-01-01", "2026-01-01").Should().BeFalse();
    }
}

public sealed class RateLimitApiTests(ThrottledApiFactory factory) : IClassFixture<ThrottledApiFactory>
{
    [Fact]
    public async Task Demo_sign_ins_are_rate_limited_and_forwarded_headers_cannot_spoof_a_new_address()
    {
        async Task<HttpResponseMessage> StartDemo(string forwardedFor)
        {
            var client = factory.CreateSessionClient();
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
            return await client.PostAsync("/api/auth/demo", null);
        }

        (await StartDemo("203.0.113.1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StartDemo("203.0.113.2")).StatusCode.Should().Be(HttpStatusCode.OK);

        var third = await StartDemo("203.0.113.3");
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await third.ErrorCodeAsync()).Should().Be("rate_limited");
    }
}
