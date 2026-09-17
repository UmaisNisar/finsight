using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FinSight.Infrastructure.Email;

/// <param name="Headers">Extra headers, for example List-Unsubscribe.</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, IReadOnlyDictionary<string, string> Headers);

/// <summary>Sending failed (connection, authentication or a rejected message). The message never includes server replies.</summary>
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException() : base("Email delivery failed.") { }
    public EmailDeliveryException(string message) : base(message) { }
    public EmailDeliveryException(string message, Exception inner) : base(message, inner) { }
}

public interface IEmailSender
{
    /// <summary>False when the server has no way to deliver email. Email features then report "not configured".</summary>
    bool IsConfigured { get; }

    /// <exception cref="EmailDeliveryException">The message could not be delivered.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

internal static class MimeMessages
{
    public static MimeMessage Build(EmailMessage message, EmailOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.From ?? throw new EmailDeliveryException("Email:From is not set.")));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        foreach (var (name, value) in message.Headers)
        {
            mime.Headers.Add(name, value);
        }

        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}

/// <summary>Delivers through an SMTP server with MailKit. Connects per message; summaries are sent a few at a time.</summary>
internal sealed partial class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => true;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(settings.Smtp.TimeoutSeconds).TotalMilliseconds };

        try
        {
            var mime = MimeMessages.Build(message, settings);
            var security = settings.Smtp.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };

            await client.ConnectAsync(settings.Smtp.Host ?? string.Empty, settings.Smtp.Port, security, cancellationToken);
            if (!string.IsNullOrEmpty(settings.Smtp.Username))
            {
                await client.AuthenticateAsync(settings.Smtp.Username, settings.Smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Server replies can echo the recipient address; log the exception type only, never the message or credentials.
            LogSendFailed(logger, ex.GetType().Name);
            throw new EmailDeliveryException("Email delivery failed.", ex);
        }
    }

    [LoggerMessage(LogLevel.Warning, "SMTP delivery failed with {ExceptionType}")]
    private static partial void LogSendFailed(ILogger logger, string exceptionType);
}

/// <summary>Writes each message as an .eml file. For local development: nothing leaves the machine.</summary>
internal sealed class PickupDirectoryEmailSender(string directory, IOptions<EmailOptions> options, TimeProvider time) : IEmailSender
{
    public bool IsConfigured => true;

    public string Directory => directory;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mime = MimeMessages.Build(message, new EmailOptions { From = settings.From ?? "finsight@localhost", FromName = settings.FromName });
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");
        try
        {
            await mime.WriteToAsync(path, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new EmailDeliveryException("Could not write the message.", ex);
        }
    }
}

/// <summary>No SMTP server and no pickup directory: email features are reported as not configured.</summary>
internal sealed class UnconfiguredEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new EmailDeliveryException("Email is not configured on this server.");
}
