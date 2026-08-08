using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Reporting;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers when the weekly digest is sent, what it says, and — most importantly — that it can never
/// take the weather workers down with it.
/// </summary>
public class WeeklyReportMailServiceTests
{
    [Test]
    public async Task NoRecipientConfigured_SendsNothing()
    {
        var mail = new RecordingEmailSender();
        var recorder = new CycleReportRecorder();
        recorder.Record(Cycle(new DateOnly(2026, 7, 10), 5, 1, "MLI-1", "MLI-2"));

        await Service(mail, recorder, to: string.Empty).SendWeeklyReportAsync();

        await Assert.That(mail.Sent).IsEmpty();
    }

    [Test]
    public async Task NothingToReport_SendsNothing()
    {
        var mail = new RecordingEmailSender();

        await Service(mail, new CycleReportRecorder()).SendWeeklyReportAsync();

        await Assert.That(mail.Sent).IsEmpty();
    }

    [Test]
    public async Task IncidentsWithoutAnyCompletedCycle_StillSend()
    {
        // A crash-loop that never finishes a cycle is exactly the week you most need the email.
        var mail = new RecordingEmailSender();
        var tracker = new FakeLifecycleTracker(new IncidentEntry(
            new DateTime(2026, 7, 10, 9, 0, 0),
            new DateTime(2026, 7, 10, 8, 45, 0),
            new DateTime(2026, 7, 10, 9, 0, 0),
            "ERROR: out of memory"));

        await Service(mail, new CycleReportRecorder(), tracker: tracker).SendWeeklyReportAsync();

        await Assert.That(mail.Sent).HasCount().EqualTo(1);
    }

    [Test]
    public async Task OneEmailCoversEveryRecordedCycle()
    {
        var mail = new RecordingEmailSender();
        var recorder = new CycleReportRecorder();
        recorder.Record(Cycle(new DateOnly(2026, 7, 6), 5, 0, "MLI-1", null));
        recorder.Record(Cycle(new DateOnly(2026, 7, 7), 3, 2, "MLI-3", "MLI-4"));

        await Service(mail, recorder).SendWeeklyReportAsync();

        await Assert.That(mail.Sent).HasCount().EqualTo(1);
        EmailMessage sent = mail.Sent[0];
        await Assert.That(sent.To).IsEqualTo("ops@example.com");
        await Assert.That(sent.From).IsEqualTo("weather@example.com");
        await Assert.That(sent.Subject).Contains("Weekly Report");
        await Assert.That(sent.Body).Contains("2026-07-06");
        await Assert.That(sent.Body).Contains("processed=5");
        await Assert.That(sent.Body).Contains("2026-07-07");
        await Assert.That(sent.Body).Contains("failed=2");
        await Assert.That(sent.Body).Contains("lastFailedStation=MLI-4");
        await Assert.That(sent.Body).Contains("no crashes or unexpected restarts detected");
    }

    [Test]
    public async Task IncidentDetailsAppearInTheBody()
    {
        var mail = new RecordingEmailSender();
        var recorder = new CycleReportRecorder();
        recorder.Record(Cycle(new DateOnly(2026, 7, 10), 1, 0, "MLI-1", null));
        var tracker = new FakeLifecycleTracker(new IncidentEntry(
            new DateTime(2026, 7, 8, 3, 0, 0),
            new DateTime(2026, 7, 7, 23, 45, 0),
            new DateTime(2026, 7, 8, 3, 0, 0),
            "ERROR: Weather worker loop failed"));

        await Service(mail, recorder, tracker: tracker).SendWeeklyReportAsync();

        string body = mail.Sent[0].Body;
        await Assert.That(body).Contains("1 crash detected");
        await Assert.That(body).Contains("2026-07-07 23:45:00");
        await Assert.That(body).Contains("2026-07-08 03:00:00");
        await Assert.That(body).Contains("ERROR: Weather worker loop failed");
    }

    [Test]
    public async Task SenderFallsBackToTheSmtpAccount()
    {
        var mail = new RecordingEmailSender();
        var recorder = new CycleReportRecorder();
        recorder.Record(Cycle(new DateOnly(2026, 7, 10), 1, 0, "MLI-1", null));

        await Service(mail, recorder, from: string.Empty, smtpUsername: "smtp-account@example.com")
            .SendWeeklyReportAsync();

        await Assert.That(mail.Sent[0].From).IsEqualTo("smtp-account@example.com");
    }

    [Test]
    public async Task SendFailure_IsLoggedNotPropagated()
    {
        // The report is a convenience; a broken SMTP server must not surface as a worker failure.
        var mail = new RecordingEmailSender { ThrowOnSend = new EmailSendException("boom") };
        var recorder = new CycleReportRecorder();
        recorder.Record(Cycle(new DateOnly(2026, 7, 10), 1, 0, "MLI-1", null));

        await Service(mail, recorder).SendWeeklyReportAsync(); // must not throw

        await Assert.That(mail.Sent).IsEmpty();
    }

    [Test]
    public async Task MissingStationsRenderAsNone()
    {
        string body = WeeklyReportMailService.BuildReportBody(
            [new CycleReportEntry(new DateOnly(2026, 7, 10), "Weather.gov", "US", 0, 0, null, null)],
            []);

        await Assert.That(body).Contains("lastProcessedStation=<none>");
        await Assert.That(body).Contains("lastFailedStation=<none>");
    }

    private static WeeklyReportMailService Service(
        IEmailSender mail,
        CycleReportRecorder recorder,
        ServiceLifecycleTracker? tracker = null,
        string to = "ops@example.com",
        string from = "weather@example.com",
        string smtpUsername = "") =>
        new(mail,
            recorder,
            tracker ?? new FakeLifecycleTracker(),
            Options.Create(new ReportOptions { To = to, From = from }),
            Options.Create(new SmtpOptions { Username = smtpUsername }),
            NullLogger<WeeklyReportMailService>.Instance);

    private static CycleReportEntry Cycle(
        DateOnly date, int processed, int failed, string? lastProcessed, string? lastFailed) =>
        new(date, "Weather.gov", "US", processed, failed, lastProcessed, lastFailed);
}
