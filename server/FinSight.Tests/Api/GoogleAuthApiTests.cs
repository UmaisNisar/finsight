using System.Net;
using System.Text.Json;
using FinSight.Api.Auth;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Tests.Api;

/// <summary>
/// Google sign-in and Gmail consent against the real authentication pipeline. Only Google's servers are faked: the tests
/// follow the redirect to Google, then call the callback the way the browser would after consent.
/// </summary>
public sealed class GoogleAuthApiTests(GoogleApiFactory factory) : IClassFixture<GoogleApiFactory>
{
    private const string GmailScopes = "openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile " + GoogleIntegrationOptions.GmailReadonlyScope;
    private const string DevRedirectUri = "http://localhost:5173/api/auth/google/callback";

    [Fact]
    public async Task The_google_handler_is_registered_when_configured()
    {
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(AuthenticationSetup.GoogleScheme)).Should().NotBeNull();
        var session = await (await factory.CreateBrowserClient().GetAsync("/api/auth/session")).JsonAsync();
        session.GetProperty("capabilities").GetProperty("googleSignIn").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Sign_in_challenges_google_with_pkce_the_dev_redirect_uri_and_basic_scopes()
    {
        var response = await factory.CreateBrowserClient().GetAsync("/api/auth/google?returnUrl=/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be("https://accounts.google.com/o/oauth2/v2/auth");

        var query = QueryHelpers.ParseQuery(location.Query);
        query["client_id"].ToString().Should().Be(GoogleApiFactory.ClientId, "the factory's dummy value must win over any user secret on this machine");
        query["redirect_uri"].ToString().Should().Be(DevRedirectUri);
        query["response_type"].ToString().Should().Be("code");
        query["scope"].ToString().Should().Be("openid email profile");
        query["code_challenge"].ToString().Should().NotBeNullOrEmpty();
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["state"].ToString().Should().NotBeNullOrEmpty();
        query.Should().NotContainKey("access_type", "sign-in never asks for offline access");

        var correlation = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)).ToLowerInvariant();
        correlation.Should().Contain("samesite=lax").And.Contain("httponly").And.Contain("path=/api/auth/google/callback");
    }

    [Fact]
    public async Task Sign_in_creates_the_user_once_and_finds_them_again_by_google_subject()
    {
        var subject = NewSubject();

        var first = factory.CreateBrowserClient();
        var callback = await SignInAsync(first, subject, "sam@example.com", "/transactions");
        callback.Headers.Location!.OriginalString.Should().Be("/transactions");

        var token = factory.Google.TokenRequests.Last();
        token["redirect_uri"].Should().Be(DevRedirectUri);
        token["code_verifier"].Should().NotBeNullOrEmpty();
        token["client_secret"].Should().Be(GoogleApiFactory.ClientSecret);

        var user = await SessionUserAsync(first);
        user.GetProperty("isDemo").GetBoolean().Should().BeFalse();
        user.GetProperty("email").GetString().Should().Be("sam@example.com");
        user.GetProperty("name").GetString().Should().Be("Sam");

        var second = factory.CreateBrowserClient();
        await SignInAsync(second, subject, "sam.rivera@example.com");
        var again = await SessionUserAsync(second);

        again.GetProperty("id").GetGuid().Should().Be(user.GetProperty("id").GetGuid());
        again.GetProperty("email").GetString().Should().Be("sam.rivera@example.com");
        (await CountUsersWithSubjectAsync(subject)).Should().Be(1);
        (await ConnectionAsync(user.GetProperty("id").GetGuid())).Should().BeNull("signing in never grants Gmail access");
    }

    [Fact]
    public async Task Signing_in_with_google_from_a_demo_session_switches_to_the_google_user()
    {
        var client = factory.CreateSessionClient(baseAddress: GoogleApiFactory.ViteOrigin);
        (await client.PostAsync("/api/auth/demo", null)).EnsureSuccessStatusCode();
        var demo = await SessionUserAsync(client);

        await SignInAsync(client, NewSubject(), "sam@example.com");
        var user = await SessionUserAsync(client);

        user.GetProperty("isDemo").GetBoolean().Should().BeFalse();
        user.GetProperty("id").GetGuid().Should().NotBe(demo.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Connecting_gmail_requires_a_session()
    {
        var response = await factory.CreateBrowserClient().GetAsync("/api/gmail/connect");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demo_users_are_sent_back_instead_of_to_google()
    {
        var client = factory.CreateSessionClient(baseAddress: GoogleApiFactory.ViteOrigin);
        (await client.PostAsync("/api/auth/demo", null)).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/gmail/connect");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/statements?gmail=demo");
    }

    [Fact]
    public async Task Connecting_gmail_asks_for_read_only_gmail_with_offline_access_and_fresh_consent()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");

        var response = await client.GetAsync("/api/gmail/connect");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        query["scope"].ToString().Split(' ').Should().BeEquivalentTo(["openid", "email", "profile", GoogleIntegrationOptions.GmailReadonlyScope]);
        query["access_type"].ToString().Should().Be("offline");
        query["prompt"].ToString().Should().Be("consent");
        query["include_granted_scopes"].ToString().Should().Be("true");
        query["login_hint"].ToString().Should().Be("sam@example.com");
        query["redirect_uri"].ToString().Should().Be(DevRedirectUri);
        query["code_challenge"].ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_granted_gmail_scope_stores_the_refresh_token_encrypted()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");
        var userId = (await SessionUserAsync(client)).GetProperty("id").GetGuid();

        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        cache.Set(GoogleTokenService.AccessTokenCacheKey(userId), "access-token-for-a-previous-grant");

        var callback = await ConnectGmailAsync(client, "sam@example.com", GmailScopes, "refresh-secret-1");

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=connected");
        var gmail = await (await client.GetAsync("/api/gmail")).JsonAsync();
        gmail.GetProperty("connected").GetBoolean().Should().BeTrue();
        gmail.GetProperty("status").GetString().Should().Be("active");

        var connection = (await ConnectionAsync(userId))!;
        connection.EncryptedRefreshToken.Should().NotContain("refresh-secret-1");
        factory.Services.GetRequiredService<ITokenProtector>().TryUnprotect(connection.EncryptedRefreshToken).Should().Be("refresh-secret-1");
        connection.Scopes.Should().Contain(GoogleIntegrationOptions.GmailReadonlyScope);
        cache.TryGetValue(GoogleTokenService.AccessTokenCacheKey(userId), out _).Should().BeFalse("a new grant must not reuse an access token from the old one");
    }

    [Fact]
    public async Task Unticking_gmail_on_the_consent_screen_connects_nothing()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");

        var callback = await ConnectGmailAsync(client, "sam@example.com", "openid https://www.googleapis.com/auth/userinfo.email", "refresh-without-gmail");

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=denied");
        (await (await client.GetAsync("/api/gmail")).JsonAsync()).GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_gmail_grant_without_a_refresh_token_connects_nothing()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");

        var callback = await ConnectGmailAsync(client, "sam@example.com", GmailScopes, refreshToken: null);

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=denied");
        (await (await client.GetAsync("/api/gmail")).JsonAsync()).GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_gmail_consent_reports_denied_and_keeps_the_session()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");
        var state = StateOf(await client.GetAsync("/api/gmail/connect"));

        var callback = await client.GetAsync($"/api/auth/google/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=denied");
        (await SessionUserAsync(client)).GetProperty("email").GetString().Should().Be("sam@example.com");
    }

    [Fact]
    public async Task Cancelling_sign_in_consent_reports_a_failed_sign_in()
    {
        var client = factory.CreateBrowserClient();
        var state = StateOf(await client.GetAsync("/api/auth/google"));

        var callback = await client.GetAsync($"/api/auth/google/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        callback.Headers.Location!.OriginalString.Should().Be("/?auth=failed");
        (await (await client.GetAsync("/api/auth/session")).JsonAsync()).GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_callback_from_another_browser_fails_the_correlation_check()
    {
        var victim = factory.CreateBrowserClient();
        await SignInAsync(victim, NewSubject(), "sam@example.com");
        var attacker = factory.CreateBrowserClient();
        await SignInAsync(attacker, NewSubject(), "mallory@example.com");
        var state = StateOf(await attacker.GetAsync("/api/gmail/connect"));
        var code = factory.Google.Issue(NewSubject(), "mallory@example.com", GmailScopes, "attacker-refresh");

        // The attacker's code and state, replayed in the victim's browser, which never received the correlation cookie.
        var callback = await victim.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=failed");
        (await (await victim.GetAsync("/api/gmail")).JsonAsync()).GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Gmail_can_come_from_a_different_google_account_than_sign_in()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");
        var before = await SessionUserAsync(client);

        var callback = await ConnectGmailAsync(client, "sam.statements@example.com", GmailScopes, "refresh-other-account", subject: NewSubject());

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=connected");
        var after = await SessionUserAsync(client);
        after.GetProperty("id").GetGuid().Should().Be(before.GetProperty("id").GetGuid());
        after.GetProperty("email").GetString().Should().Be("sam@example.com");
        (await (await client.GetAsync("/api/gmail")).JsonAsync()).GetProperty("email").GetString().Should().Be("sam.statements@example.com");
    }

    [Fact]
    public async Task A_gmail_grant_is_not_given_to_a_different_user_who_signed_in_meanwhile()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");
        var state = StateOf(await client.GetAsync("/api/gmail/connect"));

        // Another account signs in (say, in a second tab) while Google's consent screen is still open.
        await SignInAsync(client, NewSubject(), "alex@example.com");
        var code = factory.Google.Issue(NewSubject(), "sam@example.com", GmailScopes, "refresh-for-sam");
        var callback = await client.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");

        callback.Headers.Location!.OriginalString.Should().Be("/statements?gmail=failed");
        (await SessionUserAsync(client)).GetProperty("email").GetString().Should().Be("alex@example.com");
        (await (await client.GetAsync("/api/gmail")).JsonAsync()).GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("/onboarding", "/onboarding?gmail=connected")]
    [InlineData("/onboarding?step=gmail", "/onboarding?step=gmail&gmail=connected")]
    [InlineData(null, "/statements?gmail=connected")]
    [InlineData("", "/statements?gmail=connected")]
    [InlineData("//evil.example", "/statements?gmail=connected")]
    [InlineData("https://evil.example/onboarding", "/statements?gmail=connected")]
    [InlineData("/\\evil.example", "/statements?gmail=connected")]
    [InlineData("/\t/evil.example", "/statements?gmail=connected")]
    public async Task A_gmail_connect_returns_to_a_safe_return_path(string? returnTo, string expected)
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");

        var callback = await ConnectGmailAsync(client, "sam@example.com", GmailScopes, "refresh-return-to", returnTo: returnTo);

        callback.Headers.Location!.OriginalString.Should().Be(expected);
        (await (await client.GetAsync("/api/gmail")).JsonAsync()).GetProperty("connected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Every_gmail_connect_outcome_returns_to_the_same_return_path()
    {
        var client = factory.CreateBrowserClient();
        await SignInAsync(client, NewSubject(), "sam@example.com");

        var denied = await ConnectGmailAsync(client, "sam@example.com", "openid https://www.googleapis.com/auth/userinfo.email", "refresh-no-gmail", returnTo: "/onboarding");
        denied.Headers.Location!.OriginalString.Should().Be("/onboarding?gmail=denied");

        var cancelState = StateOf(await client.GetAsync("/api/gmail/connect?returnTo=%2Fonboarding"));
        var cancelled = await client.GetAsync($"/api/auth/google/callback?error=access_denied&state={Uri.EscapeDataString(cancelState)}");
        cancelled.Headers.Location!.OriginalString.Should().Be("/onboarding?gmail=denied");

        var failState = StateOf(await client.GetAsync("/api/gmail/connect?returnTo=%2Fonboarding"));
        await SignInAsync(client, NewSubject(), "alex@example.com");
        var code = factory.Google.Issue(NewSubject(), "sam@example.com", GmailScopes, "refresh-for-sam");
        var failed = await client.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(failState)}");
        failed.Headers.Location!.OriginalString.Should().Be("/onboarding?gmail=failed");

        var demo = factory.CreateSessionClient(baseAddress: GoogleApiFactory.ViteOrigin);
        (await demo.PostAsync("/api/auth/demo", null)).EnsureSuccessStatusCode();
        (await demo.GetAsync("/api/gmail/connect?returnTo=%2Fonboarding")).Headers.Location!.OriginalString.Should().Be("/onboarding?gmail=demo");
    }

    [Fact]
    public async Task Diagnostics_report_the_redirect_uri_and_origin_without_revealing_credentials()
    {
        var response = await factory.CreateBrowserClient().GetAsync("/api/auth/diagnostics");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var google = JsonDocument.Parse(body).RootElement.GetProperty("google");
        google.GetProperty("clientIdSet").GetBoolean().Should().BeTrue();
        google.GetProperty("clientIdFormatValid").GetBoolean().Should().BeTrue();
        google.GetProperty("clientSecretSet").GetBoolean().Should().BeTrue();
        google.GetProperty("handlerRegistered").GetBoolean().Should().BeTrue();
        google.GetProperty("restartRequired").GetBoolean().Should().BeFalse();
        google.GetProperty("javaScriptOrigin").GetString().Should().Be("http://localhost:5173");
        google.GetProperty("redirectUri").GetString().Should().Be(DevRedirectUri);
        body.Should().NotContain(GoogleApiFactory.ClientSecret).And.NotContain(GoogleApiFactory.ClientId);
    }

    private static string NewSubject() => $"google-{Guid.NewGuid():N}";

    private static string StateOf(HttpResponseMessage challenge)
    {
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
    }

    private async Task<HttpResponseMessage> SignInAsync(HttpClient client, string subject, string email, string returnUrl = "/")
    {
        var state = StateOf(await client.GetAsync($"/api/auth/google?returnUrl={Uri.EscapeDataString(returnUrl)}"));
        var code = factory.Google.Issue(subject, email);
        var callback = await client.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return callback;
    }

    private async Task<HttpResponseMessage> ConnectGmailAsync(HttpClient client, string email, string scope, string? refreshToken, string? subject = null, string? returnTo = null)
    {
        var state = StateOf(await client.GetAsync(returnTo is null ? "/api/gmail/connect" : $"/api/gmail/connect?returnTo={Uri.EscapeDataString(returnTo)}"));
        var code = factory.Google.Issue(subject ?? NewSubject(), email, scope, refreshToken);
        var callback = await client.GetAsync($"/api/auth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return callback;
    }

    private static async Task<JsonElement> SessionUserAsync(HttpClient client)
    {
        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        session.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        return session.GetProperty("user");
    }

    private async Task<int> CountUsersWithSubjectAsync(string subject)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<UserContext>().BeginSystemScope())
        {
            return await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().Users.CountAsync(u => u.GoogleSubject == subject);
        }
    }

    private async Task<GmailConnection?> ConnectionAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        return await scope.ServiceProvider.GetRequiredService<FinSightDbContext>().GmailConnections.AsNoTracking().SingleOrDefaultAsync();
    }
}

/// <summary>Without Google credentials the app still runs; sign-in and Gmail say they're unavailable instead of failing.</summary>
public sealed class GoogleUnconfiguredApiTests(FinSightApiFactory factory) : IClassFixture<FinSightApiFactory>
{
    [Fact]
    public async Task No_google_handler_is_registered_and_sign_in_reports_unavailable()
    {
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        (await schemes.GetSchemeAsync(AuthenticationSetup.GoogleScheme)).Should().BeNull();

        var response = await factory.CreateSessionClient().GetAsync("/api/auth/google");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/?auth=unavailable");
    }

    [Fact]
    public async Task Connecting_gmail_reports_unavailable()
    {
        var client = await factory.CreateDemoClientAsync();

        var response = await client.GetAsync("/api/gmail/connect");

        response.Headers.Location!.OriginalString.Should().Be("/statements?gmail=unavailable");
        (await client.GetAsync("/api/gmail/connect?returnTo=%2Fonboarding")).Headers.Location!.OriginalString.Should().Be("/onboarding?gmail=unavailable");
    }

    [Fact]
    public async Task Diagnostics_say_nothing_is_set_even_when_this_machine_has_user_secrets()
    {
        var diagnostics = await (await factory.CreateSessionClient().GetAsync("/api/auth/diagnostics")).JsonAsync();

        var google = diagnostics.GetProperty("google");
        google.GetProperty("clientIdSet").GetBoolean().Should().BeFalse();
        google.GetProperty("clientSecretSet").GetBoolean().Should().BeFalse();
        google.GetProperty("handlerRegistered").GetBoolean().Should().BeFalse();
        google.GetProperty("restartRequired").GetBoolean().Should().BeFalse();
        diagnostics.GetProperty("hints").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Credentials_added_while_running_need_a_restart_and_are_not_offered_before_then()
    {
        // User secrets reload live, but the Google handler is only registered at startup.
        using var running = new FinSightApiFactory();
        var client = running.CreateSessionClient();
        (await client.GetAsync("/api/auth/session")).EnsureSuccessStatusCode();

        var configuration = running.Services.GetRequiredService<IConfiguration>();
        configuration["Google:ClientId"] = "1234567890-later.apps.googleusercontent.com";
        configuration["Google:ClientSecret"] = "added-later";

        var diagnostics = await (await client.GetAsync("/api/auth/diagnostics")).JsonAsync();
        diagnostics.GetProperty("google").GetProperty("restartRequired").GetBoolean().Should().BeTrue();

        var session = await (await client.GetAsync("/api/auth/session")).JsonAsync();
        session.GetProperty("capabilities").GetProperty("googleSignIn").GetBoolean().Should().BeFalse();
        (await client.GetAsync("/api/auth/google")).Headers.Location!.OriginalString.Should().Be("/?auth=unavailable");
    }
}

public sealed class GoogleDiagnosticsProductionTests(ProductionApiFactory factory) : IClassFixture<ProductionApiFactory>
{
    [Fact]
    public async Task Diagnostics_do_not_exist_outside_development()
    {
        var response = await factory.CreateSessionClient().GetAsync("/api/auth/diagnostics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
