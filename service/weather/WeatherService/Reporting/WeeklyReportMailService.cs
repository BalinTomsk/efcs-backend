using System.Globalization;
using System.Text;
using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherService.Configuration;

namespace WeatherService.Reporting;

/// <summary>
/// Emails a weekly digest of the daily "cycle completed" summaries recorded by
/// <see cref="CycleReportRecorder"/>, plus any crash/unclean-restart incidents recorded by
/// <see cref="ServiceLifecycleTracker"/>.
///
/// <para>Fires on <c>Weather:Report:Cron</c> (default: every Friday at 08:00, server-local time —
/// the Spring <c>@Scheduled</c> equivalent). A missing recipient skips the send; so does having
/// nothing to report (no cycles AND no incidents) — but incidents alone are enough to send, since a
/// crash-loop that never completes a cycle must not go unreported. A send failure is logged, never
/// propagated: a broken SMTP server must not take the weather workers down with it.</para>
/// </summary>
public class WeeklyReportMailService : BackgroundService
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly IEmailSender _mailSender;
    private readonly CycleReportRecorder _recorder;
    private readonly ServiceLifecycleTracker _lifecycleTracker;
    private readonly ReportOptions _report;
    private readonly SmtpOptions _smtp;
    private readonly ILogger<WeeklyReportMailService> _log;

    public WeeklyReportMailService(
        IEmailSender mailSender,
        CycleReportRecorder recorder,
        ServiceLifecycleTracker lifecycleTracker,
        IOptions<ReportOptions> report,
        IOptions<SmtpOptions> smtp,
        ILogger<WeeklyReportMailService> log)
    {
        _mailSender = mailSender;
        _recorder = recorder;
        _lifecycleTracker = lifecycleTracker;
        _report = report.Value;
        _smtp = smtp.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_report.To))
        {
            _log.LogInformation("Weather:Report:To not configured; weekly report email is disabled.");
            return;
        }

        if (!CronExpression.TryParse(_report.Cron, CronFormat.IncludeSeconds, out CronExpression? schedule))
        {
            // Wrong cron text disables the email; it must not stop the service from collecting weather.
            _log.LogError("Weather:Report:Cron is not a valid 6-field cron expression; weekly report email "
                + "is disabled. cron={Cron}", _report.Cron);
            return;
        }

        _log.LogInformation("Weekly report email scheduled. to={To} cron={Cron}", _report.To, _report.Cron);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? next = schedule.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
            if (next is null)
            {
                _log.LogWarning("Weekly report cron has no future occurrence; stopping the scheduler. cron={Cron}",
                    _report.Cron);
                return;
            }

            TimeSpan delay = next.Value - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await SendWeeklyReportAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds and sends the report for whatever has been recorded so far. Public so a deployment check
    /// (or a test) can trigger one without waiting for Friday.
    /// </summary>
    public virtual async Task SendWeeklyReportAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_report.To))
        {
            _log.LogInformation("Weather:Report:To not configured; skipping weekly report email.");
            return;
        }

        IReadOnlyList<CycleReportEntry> entries = _recorder.RecentEntries();
        IReadOnlyList<IncidentEntry> incidents = _lifecycleTracker.RecentIncidents();
        if (entries.Count == 0 && incidents.Count == 0)
        {
            _log.LogInformation("No cycle data or incidents recorded since last report; skipping weekly report email.");
            return;
        }

        string from = string.IsNullOrWhiteSpace(_report.From) ? _smtp.Username : _report.From;
        var message = new EmailMessage(
            _report.To,
            string.IsNullOrWhiteSpace(from) ? null : from,
            "Weather Service Weekly Report",
            BuildReportBody(entries, incidents));

        try
        {
            await _mailSender.SendAsync(message, ct).ConfigureAwait(false);
            _log.LogInformation("Weekly report email sent. to={To} cycleEntries={Cycles} incidents={Incidents}",
                _report.To, entries.Count, incidents.Count);
        }
        catch (EmailSendException ex)
        {
            _log.LogError(ex, "Failed to send weekly report email. to={To}", _report.To);
        }
    }

    /// <summary>Renders the report body: one line per recorded cycle, then the reliability section.</summary>
    internal static string BuildReportBody(
        IReadOnlyList<CycleReportEntry> entries, IReadOnlyList<IncidentEntry> incidents)
    {
        var body = new StringBuilder("Weather service - worker cycle summary for the past ")
            .Append(entries.Count)
            .Append(entries.Count == 1 ? " entry" : " entries")
            .Append(":\n\n");

        foreach (CycleReportEntry entry in entries)
        {
            body.Append(entry.Date.ToString(DateFormat, CultureInfo.InvariantCulture)).Append(": ")
                .Append("worker=").Append(DisplayWorker(entry.Worker))
                .Append(" country=").Append(Display(entry.Country)).Append(' ')
                .Append("processed=").Append(entry.SuccessfulStations)
                .Append(" failed=").Append(entry.FailedStations)
                .Append(" lastProcessedStation=").Append(DisplayStation(entry.LastProcessedStation))
                .Append(" lastFailedStation=").Append(DisplayStation(entry.LastFailedStation))
                .Append('\n');
        }

        body.Append("\nService reliability this week: ");
        if (incidents.Count == 0)
        {
            body.Append("no crashes or unexpected restarts detected.\n");
        }
        else
        {
            body.Append(incidents.Count).Append(incidents.Count == 1 ? " crash detected\n" : " crashes detected\n");
            foreach (IncidentEntry incident in incidents)
            {
                body.Append(incident.DetectedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                    .Append(": down from ")
                    .Append(incident.DowntimeStart.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                    .Append(" to ")
                    .Append(incident.DowntimeEnd.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                    .Append(" - ")
                    .Append(incident.Description)
                    .Append('\n');
            }
        }

        return body.ToString();
    }

    private static string DisplayStation(string? station) =>
        string.IsNullOrWhiteSpace(station) ? "<none>" : station;

    private static string DisplayWorker(string? worker) =>
        string.IsNullOrWhiteSpace(worker) ? "<unknown>" : worker.ToUpperInvariant();

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
}
