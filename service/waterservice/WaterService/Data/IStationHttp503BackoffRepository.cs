using WaterService.Domain;

namespace WaterService.Data;

public interface IStationHttp503BackoffRepository
{
    Task RefreshDueAsync(DateOnly today, CancellationToken ct = default);

    Task RecordHttp503Async(
        string provider,
        string country,
        string stationMli,
        string state,
        DateOnly today,
        CancellationToken ct = default);

    Task ResetAsync(string provider, string country, string stationMli, CancellationToken ct = default);

    Task<IReadOnlyList<BackoffSummary>> SummaryByStateAsync(CancellationToken ct = default);
}
