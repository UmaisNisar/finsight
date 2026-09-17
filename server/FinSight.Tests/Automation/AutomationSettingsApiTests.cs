using System.Net;
using System.Net.Http.Json;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Email;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Automation;

public sealed class AutomationSettingsApiTests(AutomationApiFactory factory) : IClassFixture<AutomationApiFactory>
{
    private async Task<(HttpClient Client, Guid UserId)> SignInAsync()
    {
        factory.Clock.Now = DateTimeOffset.UtcNow;
        var client = await factory.CreateGoogleUserClientAsync($"user-{Guid.NewGuid():N}@example.com");
        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        return (client, session.GetProperty("user").GetProperty("id").GetGuid());
    }

    private Task<int> ConnectGmailAsync(Guid userId, GmailConnectionStatus status = GmailConnectionStatus.Active) => factory.SystemAsync(async db =>
    {
        await using var scope = factory.Services.CreateAsyncScope();
        db.GmailConnections.Add(new GmailConnection
        {
            UserId = userId,
            GoogleEmail = "sam@example.com",
            EncryptedRefreshToken = scope.ServiceProvider.GetRequiredService<ITokenProtector>().Protect("refresh"),
            Scopes = GoogleIntegrationOptions.GmailReadonlyScope,
            Status = status,
        });
        return await db.SaveChangesAsync();
    });

    private static object Settings(bool? autoScan = null, bool? autoImport = null, bool? digest = null) => new
    {
        currency = "CAD",
        dateFormat = "MMM d, yyyy",
        theme = "system",
        aiCategorizationEnabled = true,
        aiInsightsEnabled = true,
        autoScanEnabled = autoScan,
        autoImportEnabled = autoImport,
        monthlyDigestEnabled = digest,
    };

    private Task<User> UserAsync(Guid id) => factory.SystemAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    [Fact]
    public async Task Settings_include_the_automation_switches_and_whether_email_is_available()
    {
        var (client, _) = await SignInAsync();

        var settings = await (await client.GetAsync("/api/settings")).JsonAsync();

        settings.GetProperty("autoScanEnabled").GetBoolean().Should().BeFalse();
        settings.GetProperty("autoImportEnabled").GetBoolean().Should().BeFalse();
        settings.GetProperty("monthlyDigestEnabled").GetBoolean().Should().BeFalse();
        settings.GetProperty("emailConfigured").GetBoolean().Should().BeTrue();
        settings.TryGetProperty("notificationsEnabled", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Automatic_scans_need_an_active_gmail_connection()
    {
        var (client, userId) = await SignInAsync();

        var response = await client.PutAsJsonAsync("/api/settings", Settings(autoScan: true));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("gmail_not_connected");

        await ConnectGmailAsync(userId, GmailConnectionStatus.Expired);
        (await (await client.PutAsJsonAsync("/api/settings", Settings(autoScan: true))).ErrorCodeAsync()).Should().Be("gmail_auth_expired");
        (await UserAsync(userId)).Settings.AutoScanEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Automatic_import_follows_automatic_scans_and_only_counts_statements_found_after_it_was_turned_on()
    {
        var (client, userId) = await SignInAsync();
        await ConnectGmailAsync(userId);

        var importWithoutScan = await (await client.PutAsJsonAsync("/api/settings", Settings(autoScan: false, autoImport: true))).JsonAsync();
        importWithoutScan.GetProperty("autoImportEnabled").GetBoolean().Should().BeFalse("import can't be on while scans are off");

        var both = await (await client.PutAsJsonAsync("/api/settings", Settings(autoScan: true, autoImport: true))).JsonAsync();
        both.GetProperty("autoScanEnabled").GetBoolean().Should().BeTrue();
        both.GetProperty("autoImportEnabled").GetBoolean().Should().BeTrue();
        var saved = await UserAsync(userId);
        saved.AutoImportEnabledAt.Should().BeCloseTo(factory.Clock.Now, TimeSpan.FromMilliseconds(1));
        saved.NextAutoScanAt.Should().BeNull("the scheduler assigns a slot");

        // Other settings saved without the automation fields leave them alone.
        var unrelated = await (await client.PutAsJsonAsync("/api/settings", new { currency = "USD", dateFormat = "MMM d, yyyy", theme = "dark", aiCategorizationEnabled = true, aiInsightsEnabled = true })).JsonAsync();
        unrelated.GetProperty("autoScanEnabled").GetBoolean().Should().BeTrue();
        unrelated.GetProperty("autoImportEnabled").GetBoolean().Should().BeTrue();

        var scansOff = await (await client.PutAsJsonAsync("/api/settings", Settings(autoScan: false, autoImport: true))).JsonAsync();
        scansOff.GetProperty("autoImportEnabled").GetBoolean().Should().BeFalse();
        (await UserAsync(userId)).AutoImportEnabledAt.Should().BeNull();
    }

    [Fact]
    public async Task Demo_users_cannot_turn_on_automation_or_send_test_email()
    {
        var client = await factory.CreateDemoClientAsync();

        (await (await client.PutAsJsonAsync("/api/settings", Settings(digest: true))).ErrorCodeAsync()).Should().Be("demo_mode");
        (await (await client.PutAsJsonAsync("/api/settings", Settings(autoScan: true))).ErrorCodeAsync()).Should().Be("demo_mode");
        var test = await client.PostAsync("/api/settings/digest/test", null);
        test.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await test.ErrorCodeAsync()).Should().Be("demo_mode");
    }

    [Fact]
    public async Task A_test_summary_goes_to_the_signed_in_user_without_counting_as_the_months_email_and_is_rate_limited()
    {
        var (client, userId) = await SignInAsync();
        var email = (await UserAsync(userId)).Email;
        var before = factory.Email.Sent.Count;

        var response = await client.PostAsync("/api/settings/digest/test", null);

        (await response.JsonAsync()).GetProperty("sent").GetBoolean().Should().BeTrue();
        var message = factory.Email.Sent.Skip(before).Should().ContainSingle().Subject;
        message.To.Should().Be(email);
        message.Subject.Should().StartWith("[Test] Your FinSight summary for");
        message.TextBody.Should().Contain("This is a test");
        (await UserAsync(userId)).LastDigestSentFor.Should().BeNull();

        (await client.PostAsync("/api/settings/digest/test", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/settings/digest/test", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/settings/digest/test", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Summary_emails_can_only_be_turned_on_when_the_server_can_send_email()
    {
        var (client, userId) = await SignInAsync();
        factory.Email.IsConfigured = false;
        try
        {
            (await (await client.GetAsync("/api/settings")).JsonAsync()).GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
            var response = await client.PutAsJsonAsync("/api/settings", Settings(digest: true));
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.ErrorCodeAsync()).Should().Be("email_not_configured");
            (await (await client.PostAsync("/api/settings/digest/test", null)).ErrorCodeAsync()).Should().Be("email_not_configured");
        }
        finally
        {
            factory.Email.IsConfigured = true;
        }

        (await (await client.PutAsJsonAsync("/api/settings", Settings(digest: true))).JsonAsync()).GetProperty("monthlyDigestEnabled").GetBoolean().Should().BeTrue();
        (await UserAsync(userId)).Settings.MonthlyDigestEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_unsubscribe_page_asks_first_and_changes_nothing_on_get()
    {
        var (client, userId) = await SignInAsync();
        await client.PutAsJsonAsync("/api/settings", Settings(digest: true));
        var token = factory.Services.GetRequiredService<UnsubscribeTokens>().Create(userId);
        var anonymous = factory.CreateSessionClient(withCsrfHeader: false);

        var page = await anonymous.GetAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(token)}");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("<form method=\"post\"").And.Contain("Turn off summary emails");
        (await UserAsync(userId)).Settings.MonthlyDigestEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task One_click_post_turns_summaries_off_without_signing_in_or_the_csrf_header()
    {
        var (client, userId) = await SignInAsync();
        await client.PutAsJsonAsync("/api/settings", Settings(digest: true));
        var token = factory.Services.GetRequiredService<UnsubscribeTokens>().Create(userId);

        // What a mail client sends for RFC 8058: a form post, no cookies, no custom headers.
        var anonymous = factory.CreateSessionClient(withCsrfHeader: false);
        var response = await anonymous.PostAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(token)}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unsubscribed");
        var saved = await UserAsync(userId);
        saved.Settings.MonthlyDigestEnabled.Should().BeFalse();
        saved.Settings.Currency.Should().Be("CAD", "nothing else changes");

        // Posting again (mail clients retry) is harmless.
        (await anonymous.PostAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(token)}", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_only_affects_its_own_user_even_when_someone_else_is_signed_in()
    {
        var (owner, ownerId) = await SignInAsync();
        await owner.PutAsJsonAsync("/api/settings", Settings(digest: true));
        var (other, otherId) = await SignInAsync();
        await other.PutAsJsonAsync("/api/settings", Settings(digest: true));
        var token = factory.Services.GetRequiredService<UnsubscribeTokens>().Create(ownerId);

        (await other.PostAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(token)}", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await UserAsync(ownerId)).Settings.MonthlyDigestEnabled.Should().BeFalse();
        (await UserAsync(otherId)).Settings.MonthlyDigestEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Expired_and_tampered_tokens_change_nothing()
    {
        var (client, userId) = await SignInAsync();
        await client.PutAsJsonAsync("/api/settings", Settings(digest: true));
        var tokens = factory.Services.GetRequiredService<UnsubscribeTokens>();
        var token = tokens.Create(userId);
        var anonymous = factory.CreateSessionClient(withCsrfHeader: false);

        var tampered = token[..^4] + (token[^4] == 'x' ? 'y' : 'x') + token[^3..];
        foreach (var bad in new[] { tampered, "", "not-a-token" })
        {
            (await anonymous.PostAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(bad)}", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await anonymous.GetAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(bad)}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        factory.Clock.Now += UnsubscribeTokens.Lifetime + TimeSpan.FromMinutes(1);
        try
        {
            var expired = await anonymous.PostAsync($"/api/email/unsubscribe?token={Uri.EscapeDataString(token)}", null);
            expired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await expired.Content.ReadAsStringAsync()).Should().Contain("expired");
        }
        finally
        {
            factory.Clock.Now = DateTimeOffset.UtcNow;
        }

        (await UserAsync(userId)).Settings.MonthlyDigestEnabled.Should().BeTrue();
    }
}
