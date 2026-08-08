using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using WeatherService.Configuration;

namespace WeatherService.Reporting;

/// <summary>
/// MailKit-backed <see cref="IEmailSender"/> — the <c>spring-boot-starter-mail</c> equivalent.
///
/// <para>Connects per send rather than holding an idle SMTP session open: this sends one message a
/// week, so a pooled connection would only be a socket left to rot between deliveries.</para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;

    public SmtpEmailSender(IOptions<SmtpOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new EmailSendException("SMTP host is not configured.");
        }

        var mime = new MimeMessage();
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.From.Add(MailboxAddress.Parse(
            string.IsNullOrWhiteSpace(message.From) ? _options.Username : message.From));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                _options.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
                ct).ConfigureAwait(false);

            // An empty username means an unauthenticated relay (a local MTA); AUTH would be rejected.
            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
            }

            await client.SendAsync(mime, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EmailSendException($"Failed to send mail via {_options.Host}:{_options.Port}.", ex);
        }
    }
}
