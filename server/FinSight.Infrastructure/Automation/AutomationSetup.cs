using FinSight.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Automation;

public static partial class AutomationSetup
{
    /// <summary>Scheduled Gmail scans, email delivery and monthly summaries.</summary>
    public static IServiceCollection AddFinSightAutomation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AutomationOptions>().Bind(configuration.GetSection(AutomationOptions.Section));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section));
        services.AddOptions<AppOptions>().Bind(configuration.GetSection(AppOptions.Section));

        services.AddSingleton(CreateEmailSender);
        services.AddSingleton<EmailLinks>();
        services.AddSingleton<EmailAvailability>();
        services.AddSingleton<UnsubscribeTokens>();
        services.AddScoped<DigestBuilder>();
        services.AddSingleton<DigestService>();
        services.AddSingleton<AutoScanService>();
        services.AddHostedService<AutomationWorker>();
        return services;
    }

    /// <summary>
    /// SMTP when a host and sender address are configured. Otherwise, in Development (or with Email:PickupDirectory),
    /// .eml files in a local folder. Otherwise email is not configured, and email features say so.
    /// </summary>
    private static IEmailSender CreateEmailSender(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<EmailOptions>>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AutomationSetup));
        var settings = options.Value;

        if (!string.IsNullOrWhiteSpace(settings.Smtp.Host))
        {
            var local = settings.Smtp.Host is "localhost" || (System.Net.IPAddress.TryParse(settings.Smtp.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
            if (string.IsNullOrWhiteSpace(settings.From))
            {
                LogMisconfigured(logger, "Email:From is not set");
            }
            else if (settings.Smtp.Security == SmtpSecurity.None && !local)
            {
                LogMisconfigured(logger, "Email:Smtp:Security=None is only allowed for a relay on localhost");
            }
            else
            {
                return ActivatorUtilities.CreateInstance<SmtpEmailSender>(services);
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.PickupDirectory) || environment.IsDevelopment())
        {
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.PickupDirectory) ? Path.Combine(".data", "mail") : settings.PickupDirectory,
                environment.ContentRootPath);
            return new PickupDirectoryEmailSender(directory, options, services.GetRequiredService<TimeProvider>());
        }

        return new UnconfiguredEmailSender();
    }

    [LoggerMessage(LogLevel.Warning, "Email is not configured: {Reason}")]
    private static partial void LogMisconfigured(ILogger logger, string reason);
}
