using Microsoft.Extensions.Configuration;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Processing;

namespace WeatherService.Tests;

/// <summary>
/// Covers the alias table that stands in for Spring's <c>${VAR:default}</c> placeholders — the reason
/// one <c>.env</c> file drives either the Java or the .NET service unchanged.
/// </summary>
public class EnvironmentAliasesTests
{
    [Test]
    public async Task FlatVariables_MapOntoTheirConfigurationKeys()
    {
        IConfiguration source = Build(new Dictionary<string, string?>
        {
            ["SMTP_HOST"] = "smtp.example.com",
            ["REPORT_EMAIL_TO"] = "ops@example.com",
            ["GOOGLE_WEATHER_DAILY_LIMIT"] = "42",
        });

        var resolved = EnvironmentAliases.Resolve(source).ToDictionary(kv => kv.Key, kv => kv.Value);

        await Assert.That(resolved["Smtp:Host"]).IsEqualTo("smtp.example.com");
        await Assert.That(resolved["Weather:Report:To"]).IsEqualTo("ops@example.com");
        await Assert.That(resolved["Weather:Worker:DailyLimit:GoogleWeather"]).IsEqualTo("42");
    }

    [Test]
    public async Task ProviderTogglesAndTimeoutsBindToTheirTypes()
    {
        // These only work if the flat strings survive the bool/int binding, so assert through Options.
        IConfiguration source = Build(new Dictionary<string, string?>
        {
            ["GOOGLE_WEATHER_ENABLE"] = "false",
            ["WEATHER_CANADA_ENABLE"] = "true",
            ["OPEN_METEO_TIMEOUT"] = "5",
        });

        var merged = new ConfigurationBuilder()
            .AddConfiguration(source)
            .AddInMemoryCollection(EnvironmentAliases.Resolve(source))
            .Build();

        var options = new WorkerOptions();
        merged.GetSection(WorkerOptions.SectionName).Bind(options);

        await Assert.That(options.Enable.GoogleWeather).IsFalse();
        await Assert.That(options.Enable.WeatherCanada).IsTrue();
        // Untouched providers keep their opt-out-only default.
        await Assert.That(options.Enable.WeatherGov).IsTrue();

        await Assert.That(options.Timeout.OpenMeteo).IsEqualTo(5);
        await Assert.That(StationWorker.CalculateDelayMs(options.Timeout.OpenMeteo, options.DailyLimit.OpenMeteo))
            .IsEqualTo(5000L);
        // Untouched providers stay on the derived gap.
        await Assert.That(options.Timeout.WeatherGov).IsZero();
    }

    [Test]
    public async Task BlankAndAbsentVariables_LeaveTheDefaultsAlone()
    {
        // Mapping an empty variable would clobber a real appsettings default with an empty string.
        IConfiguration source = Build(new Dictionary<string, string?>
        {
            ["SMTP_HOST"] = "   ",
        });

        await Assert.That(EnvironmentAliases.Resolve(source)).IsEmpty();
    }

    [Test]
    public async Task AliasedValues_WinOverAppsettings()
    {
        IConfiguration baseline = Build(new Dictionary<string, string?>
        {
            ["Weather:Worker:VisualCrossingApiKey"] = "from-appsettings",
            ["VISUAL_CROSSING_API_KEY"] = "from-environment",
        });

        var merged = new ConfigurationBuilder()
            .AddConfiguration(baseline)
            .AddInMemoryCollection(EnvironmentAliases.Resolve(baseline))
            .Build();

        await Assert.That(merged["Weather:Worker:VisualCrossingApiKey"]).IsEqualTo("from-environment");
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
