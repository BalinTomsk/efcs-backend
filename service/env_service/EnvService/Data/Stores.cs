namespace EnvService.Data;

/// <summary>
/// MySQL <c>envdata</c>: everything env_service reads and writes there, through envdata's procedures and views only
/// (<c>envfish-db/mysql/envdata</c>). <see cref="MySqlEnvStore"/> is the real one; tests use fakes.
/// </summary>
public interface IEnvStore
{
    /// <summary><c>sp_station_mirror(rows)</c>; returns the elements sent.</summary>
    Task<int> MirrorStationsAsync(IReadOnlyList<StationMaster> stations, CancellationToken ct);

    /// <summary><c>sp_fill_flow_state(NULL, NULL, NULL)</c>: as of now, all stations, skipped when younger than 6 h.</summary>
    Task<FillResult> FillFlowAsync(CancellationToken ct);

    /// <summary><c>sp_fill_pressure_state(NULL, NULL, NULL, NULL)</c>: 3 days back .. 15 ahead, skipped when younger than 6 h.</summary>
    Task<FillResult> FillPressureAsync(CancellationToken ct);

    /// <summary><c>v_job_run_pending</c>, oldest first.</summary>
    Task<IReadOnlyList<PendingJob>> PendingJobsAsync(CancellationToken ct);

    /// <summary><c>sp_job_run_handed_over(upTo)</c>; returns the cycles marked.</summary>
    Task<int> MarkHandedOverAsync(long upToJobRunId, CancellationToken ct);

    /// <summary><c>v_station_state</c>: one row per station with a reading (~11,000).</summary>
    Task<IReadOnlyList<StationStateRow>> StationStatesAsync(CancellationToken ct);

    /// <summary><c>v_station_day</c> (3 back .. 15 ahead, ~150,000 rows), streamed.</summary>
    IAsyncEnumerable<StationDayRow> StationDaysAsync(CancellationToken ct);
}

/// <summary>
/// SQL Server: env_service's boundary there is schema <c>env</c> only (<c>envfish-db/mssql</c>, the [env] blocks).
/// <see cref="SqlFishStore"/> is the real one; tests use fakes.
/// </summary>
public interface IFishStore
{
    /// <summary><c>env.fn_station_list()</c>: the station master.</summary>
    Task<IReadOnlyList<StationMaster>> StationListAsync(CancellationToken ct);

    /// <summary><c>env.sp_push_station_state</c> with one table-valued parameter; returns the rows written.</summary>
    Task<int> PushStationStatesAsync(IReadOnlyList<StationStateRow> rows, CancellationToken ct);

    /// <summary><c>env.sp_push_station_day</c> with one table-valued parameter; returns the rows written.</summary>
    Task<int> PushStationDaysAsync(IReadOnlyList<StationDayRow> rows, CancellationToken ct);

    /// <summary><c>env.sp_station_day_trim(from)</c>; returns the rows deleted.</summary>
    Task<int> TrimStationDaysAsync(DateOnly from, CancellationToken ct);

    /// <summary>
    /// <c>env.sp_push_species</c> (wraps <c>dbo.spPushSpeciesFromLakeToStation</c>): 0 = ran, -1 = the fish model still
    /// runs on the legacy path (<c>env.Setting probability_source</c> is not <c>env</c>).
    /// </summary>
    Task<int> PushSpeciesAsync(CancellationToken ct);

    /// <summary>
    /// <c>env.sp_update_probability</c>: rows of <c>fish_location.today</c> changed; -1 = legacy path (nothing written);
    /// -2 = another run holds the lock.
    /// </summary>
    Task<int> UpdateProbabilityAsync(CancellationToken ct);
}
