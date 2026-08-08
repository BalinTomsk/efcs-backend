namespace WeatherService.Configuration;

/// <summary>
/// SMTP transport settings, bound from <c>Smtp</c>. Mirrors the Spring <c>spring.mail.*</c> block.
///
/// <para>Everything defaults to blank on purpose: SMTP is optional. With no host configured the app
/// still starts and runs the weather workers normally — only the weekly report email is skipped (see
/// <c>WeeklyReportMailService</c>).</para>
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Upgrade the connection with STARTTLS, matching <c>mail.smtp.starttls.enable=true</c>.</summary>
    public bool StartTls { get; set; } = true;

    /// <summary>Whether SMTP is configured well enough to attempt a send.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
