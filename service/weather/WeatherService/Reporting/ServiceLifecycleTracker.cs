using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherService.Configuration;

namespace WeatherService.Reporting;

/// <summary>
/// Detects whether the previous run of this service shut down cleanly or crashed, and keeps a short
/// rolling history of crash incidents for the weekly report email.
///
/// <para><b>How detection works:</b> a two-line marker file is written <c>RUNNING|&lt;startedAt&gt;</c>
/// on every startup (<see cref="Init"/>) and rewritten <c>CLEAN|&lt;shutdownAt&gt;</c> on graceful
/// shutdown (<see cref="OnShutdown"/>, reached when the host receives SIGTERM and gets to run its
/// shutdown phase). If the NEXT startup finds the marker still saying <c>RUNNING</c>, the previous
/// process never got that chance — it crashed (OOM-killed, unhandled fault, <c>kill -9</c>, host
/// reboot, or a forceful container removal that skips SIGTERM). The incident's description comes from
/// the last Warning/Error line in the previous run's log, and its downtime start from that line's
/// timestamp.</para>
///
/// <para><b>This only works if the deploy tooling stops the container gracefully</b> (SIGTERM, e.g.
/// <c>docker stop</c>) rather than force-killing it (<c>docker rm -f</c> / <c>docker kill</c> send
/// SIGKILL immediately) — a force-killed deploy is indistinguishable from a crash here, since the
/// shutdown hook never runs either way. See <c>docs/do-update.md</c>.</para>
///
/// <para>State is a plain file under <c>Weather:Lifecycle:StateDir</c>, not a database table. Nothing
/// here can fail startup: unwritable or missing state is logged and skipped.</para>
///
/// <para>Registered as the FIRST hosted service so it starts before the workers and — since hosted
/// services stop in reverse order — writes its clean marker after they have all stopped.</para>
/// </summary>
public class ServiceLifecycleTracker : IHostedService
{
    private const string MarkerFile = "lifecycle.marker";
    private const string IncidentsFile = "incidents.log";
    private const string Running = "RUNNING";
    private const string Clean = "CLEAN";
    private const int LogTailLines = 500;
    private const int RetentionDays = 7;
    private const int MaxDescriptionLength = 200;

    private readonly LifecycleOptions _options;
    private readonly ILogger<ServiceLifecycleTracker> _log;
    private readonly Lock _gate = new();

    public ServiceLifecycleTracker(IOptions<LifecycleOptions> options, ILogger<ServiceLifecycleTracker> log)
    {
        _options = options.Value;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Init();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        OnShutdown();
        return Task.CompletedTask;
    }

    /// <summary>Records the previous run's crash (if any), then marks this run as in progress.</summary>
    internal void Init()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex,
                "Could not create lifecycle state dir; crash tracking disabled this run. dir={Dir}", StateDir);
            return;
        }

        DetectPreviousCrash();
        WriteMarker(Running, DateTime.Now);
    }

    /// <summary>Marks this run as having ended on purpose, so the next startup does not report a crash.</summary>
    internal void OnShutdown() => WriteMarker(Clean, DateTime.Now);

    /// <summary>
    /// Incidents detected within the last 7 days, oldest first. Reading also prunes the file, so the
    /// history cannot grow without bound.
    /// </summary>
    public virtual IReadOnlyList<IncidentEntry> RecentIncidents()
    {
        lock (_gate)
        {
            string path = IncidentsPath;
            if (!File.Exists(path))
            {
                return [];
            }

            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            var kept = new List<IncidentEntry>();
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (ParseIncidentLine(line) is { } entry && entry.DetectedAt > cutoff)
                    {
                        kept.Add(entry);
                    }
                }
                WriteIncidents(kept);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Failed reading incidents log. file={File}", path);
                return [];
            }
            return kept;
        }
    }

    private void DetectPreviousCrash()
    {
        string[] marker = ReadMarker();
        if (marker.Length == 0 || !string.Equals(marker[0], Running, StringComparison.Ordinal))
        {
            return; // first-ever startup, or the previous run shut down cleanly
        }

        string logFile = _options.LogFile;
        string description = ExtractIncidentDescription(logFile);
        DateTime now = DateTime.Now;
        DateTime downtimeStart = ExtractLastLogTimestamp(logFile) ?? ParseMarkerTimestamp(marker) ?? now;

        AppendIncident(new IncidentEntry(now, downtimeStart, now, description));
        _log.LogWarning(
            "Detected an unclean shutdown of the previous run (crash). downtimeStart={DowntimeStart} description={Description}",
            downtimeStart, description);
    }

    /// <summary>
    /// Summarises why the previous run died, from the last Warning/Error/Fatal entry it logged.
    ///
    /// <para>Reads the structured log written by the Serilog file sink, so the field names are
    /// Serilog's compact-format ones: <c>@l</c> level (absent for Information), <c>@m</c> rendered
    /// message, <c>@t</c> timestamp.</para>
    /// </summary>
    private string ExtractIncidentDescription(string logFile)
    {
        if (!File.Exists(logFile))
        {
            return "no log data available";
        }

        try
        {
            string? lastIssue = null;
            foreach (string line in TailLines(logFile, LogTailLines))
            {
                if (ParseJsonQuietly(line) is not { } entry)
                {
                    continue;
                }

                string level = ReadString(entry, "@l") ?? "Information";
                string message = ReadString(entry, "@m") ?? ReadString(entry, "@mt") ?? string.Empty;

                if (IsIssueLevel(level) && !string.IsNullOrWhiteSpace(message))
                {
                    lastIssue = level.ToUpperInvariant() + ": " + Truncate(message, MaxDescriptionLength);
                }
            }
            return lastIssue ?? "no errors or warnings recorded before the restart";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Failed reading previous log file for crash description. file={File}", logFile);
            return "no log data available";
        }
    }

    private DateTime? ExtractLastLogTimestamp(string logFile)
    {
        if (!File.Exists(logFile))
        {
            return null;
        }

        try
        {
            List<string> lines = TailLines(logFile, LogTailLines);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (ParseJsonQuietly(lines[i]) is not { } entry)
                {
                    continue;
                }
                if (ParseTimestampQuietly(ReadString(entry, "@t")) is { } timestamp)
                {
                    return timestamp;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Failed reading previous log file for downtime timestamp. file={File}", logFile);
        }
        return null;
    }

    /// <summary>Reads the last <paramref name="maxLines"/> lines without buffering the whole file.</summary>
    private static List<string> TailLines(string file, int maxLines)
    {
        var tail = new Queue<string>(maxLines);
        using var reader = new StreamReader(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == maxLines)
            {
                tail.Dequeue();
            }
            tail.Enqueue(line);
        }

        return [.. tail];
    }

    private static bool IsIssueLevel(string level) =>
        level.StartsWith("Warn", StringComparison.OrdinalIgnoreCase)
        || level.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
        || level.StartsWith("Fatal", StringComparison.OrdinalIgnoreCase);

    private static JsonElement? ParseJsonQuietly(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ParseTimestampQuietly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        // Serilog writes "@t" as a UTC instant; RoundtripKind preserves that Kind so ToLocalTime lines the
        // incident up with the local-clock timestamps everything else in this file uses.
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out DateTime parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    private static DateTime? ParseMarkerTimestamp(string[] marker) =>
        marker.Length < 2 ? null : ParseLocalTimestamp(marker[1]);

    private static DateTime? ParseLocalTimestamp(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed
            : null;

    private string[] ReadMarker()
    {
        string path = MarkerPath;
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Failed reading lifecycle marker. file={File}", path);
            return [];
        }
    }

    private void WriteMarker(string state, DateTime at)
    {
        string path = MarkerPath;
        try
        {
            File.WriteAllLines(path, [state, Format(at)], Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _log.LogWarning(ex, "Failed writing lifecycle marker. file={File} state={State}", path, state);
        }
    }

    private void AppendIncident(IncidentEntry entry)
    {
        string path = IncidentsPath;
        try
        {
            File.AppendAllText(path, FormatIncident(entry) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _log.LogWarning(ex, "Failed appending to incidents log. file={File}", path);
        }
    }

    private void WriteIncidents(List<IncidentEntry> entries) =>
        File.WriteAllLines(IncidentsPath, entries.Select(FormatIncident), Encoding.UTF8);

    private static string FormatIncident(IncidentEntry entry) => string.Join('|',
        Format(entry.DetectedAt),
        Format(entry.DowntimeStart),
        Format(entry.DowntimeEnd),
        Sanitize(entry.Description));

    private static IncidentEntry? ParseIncidentLine(string line)
    {
        string[] parts = line.Split('|', 4);
        if (parts.Length != 4)
        {
            return null;
        }
        if (ParseLocalTimestamp(parts[0]) is not { } detectedAt
            || ParseLocalTimestamp(parts[1]) is not { } downtimeStart
            || ParseLocalTimestamp(parts[2]) is not { } downtimeEnd)
        {
            return null;
        }
        return new IncidentEntry(detectedAt, downtimeStart, downtimeEnd, parts[3]);
    }

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string Sanitize(string text) =>
        text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    private string StateDir => _options.StateDir;

    private string MarkerPath => Path.Combine(StateDir, MarkerFile);

    private string IncidentsPath => Path.Combine(StateDir, IncidentsFile);
}
