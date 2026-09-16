using FinSight.Infrastructure.Gmail;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;

namespace FinSight.Api.Auth;

public static class AuthenticationSetup
{
    public const string GoogleCallbackPath = "/api/auth/google/callback";

    public static IServiceCollection AddFinSightAuthentication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddScoped<GoogleAccountLinker>();

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

                // Google redirects back with a top-level GET, which Lax cookies survive.
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

                options.Events.OnCreatingTicket = context =>
                    context.HttpContext.RequestServices.GetRequiredService<GoogleAccountLinker>().OnCreatingTicketAsync(context);

                options.Events.OnRemoteFailure = context =>
                {
                    var intent = context.Properties?.Items.TryGetValue(GoogleAccountLinker.IntentKey, out var value) == true ? value : null;
                    context.Response.Redirect(intent == GoogleAccountLinker.GmailIntent ? "/statements?gmail=failed" : "/?auth=failed");
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

    public static string GoogleScheme => GoogleDefaults.AuthenticationScheme;
}
