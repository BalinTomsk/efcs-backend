using Microsoft.Extensions.Logging;
using WaterService.Data;
using WaterService.Domain;

namespace WaterService.Processing;

/// <summary>
/// Maintains per-station HTTP 503 backoff state for water providers.
/// </summary>
public sealed class StationHttp503BackoffService
{
    private readonly IStationHttp503BackoffRepository _repository;
    private readonly ILogger<StationHttp503BackoffService> _log;

    public StationHttp503BackoffService(
        IStationHttp503BackoffRepository repository,
        ILogger<StationHttp503BackoffService> log)
    {
        _repository = repository;
        _log = log;
    }

    public Task RefreshDueAsync(DateOnly today, CancellationToken ct = default) =>
        _repository.RefreshDueAsync(today, ct);

    public async Task RecordHttp503Async(
        string provider,
        string country,
        StationRef station,
        DateOnly today,
        CancellationToken ct = default)
    {
        await _repository.RecordHttp503Async(provider, country, station.Mli, station.State, today, ct)
            .ConfigureAwait(false);
        _log.LogWarning(
            "Recorded station HTTP 503. provider={Provider} country={Country} station={Station} state={State}",
            provider, country, station.Mli, station.State);
    }

    public async Task RecordProcessedAsync(
        string provider,
        string country,
        StationRef station,
        CancellationToken ct = default)
    {
        await _repository.ResetAsync(provider, country, station.Mli, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Reset station HTTP 503 backoff after successful processing. provider={Provider} country={Country} station={Station} state={State}",
            provider, country, station.Mli, station.State);
    }

    public Task<IReadOnlyList<BackoffSummary>> SummaryByStateAsync(CancellationToken ct = default) =>
        _repository.SummaryByStateAsync(ct);
}
