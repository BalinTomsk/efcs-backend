namespace WeatherService.Reporting;

/// <summary>
/// Keeps the most recently completed cycle summaries in memory so the weekly report email can list
/// one entry per day per provider.
///
/// <para>Not persisted: a process restart (e.g. a mid-week deploy) clears it, so a report generated
/// after a restart only covers cycles completed since then. Crash <em>incidents</em> are persisted
/// separately by <see cref="ServiceLifecycleTracker"/> — a crash-loop that never finishes a cycle
/// still gets reported.</para>
/// </summary>
public class CycleReportRecorder
{
    internal const int MaxEntriesPerWorker = 7;
    /// <summary>Derived from <see cref="Processing.StationWorker.WorkerCount"/> rather than a
    /// hand-maintained constant, which previously went stale as providers were added (fixed at 2 while
    /// the worker count grew to 6) and silently shrank the report's effective window well below a
    /// week.</summary>
    internal static readonly int MaxEntries = MaxEntriesPerWorker * Processing.StationWorker.WorkerCount;

    private readonly Queue<CycleReportEntry> _entries = new();
    private readonly Lock _gate = new();

    public virtual void Record(CycleReportEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>The retained entries, oldest first.</summary>
    public virtual IReadOnlyList<CycleReportEntry> RecentEntries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
