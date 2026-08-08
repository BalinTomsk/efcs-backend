using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherService.Configuration;

namespace WeatherService.Processing;

/// <summary>
/// Tracks how much of each provider's daily API allowance has actually been spent, in a file that
/// survives restarts so a crash-loop cannot re-spend a paid quota.
///
/// <para><strong>Budget is consumed one station at a time, immediately before that station is
/// fetched</strong> — never booked up front for a whole cycle. The up-front version (inherited from
/// the Java service) charged the entire daily limit at cycle start, so any restart forfeited whatever
/// the interrupted cycle had not yet used: on 2026-08-08 three restarts burned 3,200 station-slots to
/// do ~154 stations of real work, and the service then sat idle until the next UTC day. Charging per
/// station means an interrupted cycle costs exactly what it used, and that stays true after a hard
/// kill, where nothing gets the chance to credit anything back.</para>
///
/// <para>If the ledger cannot be written the provider is skipped rather than run unmetered — an
/// unwritable state directory is precisely the condition under which a restart loop would otherwise
/// re-spend the budget over and over.</para>
/// </summary>
public class WeatherApiUsageTracker
{
    private const string UsageFile = "api-usage.log";
    private const int RetentionDays = 8;

    private readonly LifecycleOptions _options;
    private readonly ILogger<WeatherApiUsageTracker> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeatherApiUsageTracker(IOptions<LifecycleOptions> options, ILogger<WeatherApiUsageTracker> log)
    {
        _options = options.Value;
        _log = log;
    }

    /// <summary>How much of a provider's day is left, for sizing the cycle and for logging.</summary>
    /// <param name="UsedToday">Stations already charged to this provider today.</param>
    /// <param name="DailyLimit">The configured ceiling.</param>
    /// <param name="Remaining">What this cycle may still spend.</param>
    /// <param name="Persisted">Whether the ledger is readable/writable; <c>false</c> forces a skip.</param>
    public readonly record struct UsageSnapshot(int UsedToday, int DailyLimit, int Remaining, bool Persisted);

    /// <summary>Reads today's usage without charging anything.</summary>
    public virtual async Task<UsageSnapshot> SnapshotAsync(
        string provider, DateOnly date, int dailyLimit, CancellationToken ct = default)
    {
        int safeLimit = Math.Max(0, dailyLimit);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(UsageDir);
                int used = UsedOn(ReadRetainedEntries(date), provider, date);
                return new UsageSnapshot(used, safeLimit, Math.Max(0, safeLimit - used), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogLedgerFailure(ex, provider, safeLimit);
                return new UsageSnapshot(0, safeLimit, 0, false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Charges one station against the provider's daily allowance.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the station may be fetched. <c>false</c> means the allowance is spent (or the
    /// ledger is unwritable) and the caller must stop — nothing was charged.
    /// </returns>
    public virtual async Task<bool> TryConsumeAsync(
        string provider, DateOnly date, int dailyLimit, CancellationToken ct = default)
    {
        int safeLimit = Math.Max(0, dailyLimit);
        if (safeLimit == 0)
        {
            return false;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(UsageDir);
                List<UsageEntry> entries = ReadRetainedEntries(date);
                if (UsedOn(entries, provider, date) >= safeLimit)
                {
                    return false;
                }

                // One aggregated row per (date, provider): incremented in place, so the file stays a
                // few lines long however many stations a day runs.
                int index = entries.FindIndex(e => e.Date == date && e.Provider == provider);
                if (index >= 0)
                {
                    entries[index] = entries[index] with { Reserved = entries[index].Reserved + 1 };
                }
                else
                {
                    entries.Add(new UsageEntry(date, provider, 1));
                }

                // Written before the fetch, so a crash mid-station costs that station's slot rather
                // than letting the restart re-spend it.
                WriteEntries(entries);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogLedgerFailure(ex, provider, safeLimit);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LogLedgerFailure(Exception ex, string provider, int dailyLimit) => _log.LogError(ex,
        "Could not read/write the weather API usage ledger; skipping provider to avoid exceeding the "
        + "daily limit after restart. provider={Provider} dailyLimit={DailyLimit} stateDir={StateDir}",
        provider, dailyLimit, _options.StateDir);

    private static int UsedOn(List<UsageEntry> entries, string provider, DateOnly date) =>
        entries.Where(entry => entry.Date == date && entry.Provider == provider).Sum(entry => entry.Reserved);

    private List<UsageEntry> ReadRetainedEntries(DateOnly today)
    {
        string path = UsagePath;
        if (!File.Exists(path))
        {
            return [];
        }

        DateOnly cutoff = today.AddDays(-RetentionDays);
        var entries = new List<UsageEntry>();
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (ParseLine(line) is { } entry && entry.Date > cutoff)
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    private void WriteEntries(List<UsageEntry> entries)
    {
        IEnumerable<string> lines = entries.Select(entry => string.Join('|',
            entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Sanitize(entry.Provider),
            entry.Reserved.ToString(CultureInfo.InvariantCulture)));

        File.WriteAllLines(UsagePath, lines, Encoding.UTF8);
    }

    private static UsageEntry? ParseLine(string line)
    {
        string[] parts = line.Split('|', 3);
        if (parts.Length != 3)
        {
            return null;
        }
        if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int reserved))
        {
            return null;
        }
        return new UsageEntry(date, parts[1], reserved);
    }

    private static string Sanitize(string text) =>
        text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');

    private string UsageDir => _options.StateDir;

    private string UsagePath => Path.Combine(UsageDir, UsageFile);

    private readonly record struct UsageEntry(DateOnly Date, string Provider, int Reserved);
}
