using TUnit.Core;
using WaterApi.Status;

namespace WaterApi.Tests;

/// <summary>
/// STATUS_INTERVAL_SECONDS is never honoured below one minute (bounds RabbitMQ traffic). The same helper is copied into
/// waterservice, weather, news_idx and news_search.
/// </summary>
public class ServiceStatusIntervalTests
{
    [Test]
    [Arguments(10, 60)]
    [Arguments(59, 60)]
    [Arguments(60, 60)]
    [Arguments(300, 300)]
    public async Task Interval_IsNeverBelowOneMinute(int configured, int expected)
    {
        await Assert.That(ServiceStatusRegistration.ReportIntervalSeconds(configured)).IsEqualTo(expected);
    }
}
