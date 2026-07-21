namespace WaterService.Domain;

public sealed record BackoffSummary(string State, string BackoffStage, long StationCount);
