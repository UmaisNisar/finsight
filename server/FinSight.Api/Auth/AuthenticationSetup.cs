using FinSight.Infrastructure.Gmail;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;

namespace FinSight.Api.Auth;

public static partial class AuthenticationSetup
{
    public const string GoogleCallbackPath = "/api/auth/google/callback";

    public static IServiceCollection AddFinSightAuthentication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddScoped<GoogleAccountLinker>();
        services.AddScoped<SessionService>();

        var google = configuration.GetSection(GoogleIntegrationOptions.Section).Get<GoogleIntegrationOptions>() ?? new GoogleIntegrationOptions();
        var authentication = services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // "__Host-" requires Secure, path "/" and no domain: the browser then refuses to send it to subdomains or over HTTP.
                options.Cookie.Name = environment.IsDevelopment() ? "finsight.session" : "__Host-finsight";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;

                // An API answers with status codes, never login redirects.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // The handler is registered once at startup. Setting the client id later (user secrets reload live) does not add it,
        // which is why availability is read from the registered schemes (IsGoogleAvailableAsync), not from configuration.
        if (google.IsConfigured)
        {
            authentication.AddGoogle(options =>
            {
                options.ClientId = google.ClientId!;
                options.ClientSecret = google.ClientSecret!;
                options.CallbackPath = GoogleCallbackPath;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("email");
                options.Scope.Add("profile");

                // The OAuth handler (unlike OpenID Connect) sets no nonce cookie, only this correlation cookie. Its default,
                // SameSite=None, is rejected by browsers without Secure, so sign-in over http://localhost would fail with
                // "Correlation failed". Google returns with a top-level GET navigation, which Lax cookies survive.
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

                options.Events.OnCreatingTicket = context =>
                    context.HttpContext.RequestServices.GetRequiredService<GoogleAccountLinker>().OnCreatingTicketAsync(context);

                // error=access_denied: the user pressed Cancel on Google's consent screen.
                options.Events.OnAccessDenied = context =>
                {
                    var gmail = IntentOf(context.Properties) == GoogleAccountLinker.GmailIntent;
                    context.Response.Redirect(gmail ? GoogleAccountLinker.GmailRedirect(context.Properties, "denied") : "/?auth=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };

                options.Events.OnRemoteFailure = context =>
                {
                    // Exceptions thrown while creating the ticket arrive without properties; the protected state still has the intent.
                    var properties = context.Properties ?? ((OAuthOptions)context.Options).StateDataFormat.Unprotect(context.Request.Query["state"]);
                    var gmail = IntentOf(properties) == GoogleAccountLinker.GmailIntent;

                    // Framework and linker messages ("Correlation failed.", token endpoint error codes) carry no tokens or personal
                    // data; other exceptions (database, network) might, so only their type is logged.
                    var failure = context.Failure;
                    LogRemoteFailure(
                        context.HttpContext.RequestServices.GetRequiredService<ILogger<GoogleAccountLinker>>(),
                        gmail ? GoogleAccountLinker.GmailIntent : GoogleAccountLinker.SignInIntent,
                        failure?.GetType().Name ?? "unknown",
                        failure is AuthenticationFailureException or InvalidOperationException ? failure.Message : "(details withheld)");

                    context.Response.Redirect(gmail ? GoogleAccountLinker.GmailRedirect(properties, "failed") : "/?auth=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    public static bool IsGoogleConfigured(this IConfiguration configuration) =>
        (configuration.GetSection(GoogleIntegrationOptions.Section).Get<GoogleIntegrationOptions>() ?? new GoogleIntegrationOptions()).IsConfigured;

    /// <summary>True when the Google handler is registered, so a challenge can actually be issued.</summary>
    public static async Task<bool> IsGoogleAvailableAsync(this IAuthenticationSchemeProvider schemes) =>
        await schemes.GetSchemeAsync(GoogleScheme) is not null;

    /// <summary>The redirect URI the Google handler sends for this request. It must be registered exactly in Google Cloud Console.</summary>
    public static string GoogleRedirectUri(this HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{GoogleCallbackPath}";

    public static string GoogleScheme => GoogleDefaults.AuthenticationScheme;

    private static string? IntentOf(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(GoogleAccountLinker.IntentKey, out var value) == true ? value : null;

    [LoggerMessage(LogLevel.Warning, "Google {Intent} did not complete: {FailureType} {FailureMessage}")]
    private static partial void LogRemoteFailure(ILogger logger, string intent, string failureType, string failureMessage);
}
