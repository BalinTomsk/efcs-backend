namespace WeatherService.Reporting;

/// <summary>A plain-text email to send.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="From">Sender address, or <c>null</c> to let the transport pick one.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Body">Plain-text body.</param>
public sealed record EmailMessage(string To, string? From, string Subject, string Body);

/// <summary>
/// Sends plain-text mail. The seam that lets <see cref="WeeklyReportMailService"/> be tested without
/// an SMTP server — the counterpart of mocking Spring's <c>JavaMailSender</c>.
/// </summary>
public interface IEmailSender
{
    /// <summary>Whether a transport is configured at all; <c>false</c> means every send is a no-op.</summary>
    bool IsConfigured { get; }

    /// <summary>Sends one message.</summary>
    /// <exception cref="EmailSendException">The transport refused or could not deliver the message.</exception>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Wraps any transport-level mail failure, so callers catch one type rather than MailKit's.</summary>
public sealed class EmailSendException : Exception
{
    public EmailSendException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
