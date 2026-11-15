using Astra.Demo;

namespace MonitoringApi.Tests;

public class DemoSeriesTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LatestScenariosMatchTheActualPrototypeSeverityBoundaries()
    {
        var samples = DemoSeries.Samples(Anchor).ToList();
        foreach (var scenario in DemoSeries.Scenarios)
        {
            var latest = samples.Where(x => x.Pin == scenario.Pin).MaxBy(x => x.At)!;
            Assert.Equal(0, latest.Slot);
            Assert.Equal(scenario.Bpm, latest.Bpm);
            Assert.Equal(scenario.Temperature, latest.Temperature);
            Assert.Equal(scenario.Humidity, latest.Humidity);
            Assert.Equal(Anchor.AddMinutes(-scenario.AgeMinutes), latest.At);
            Assert.Equal(scenario.State, DemoSeries.Severity(latest.Bpm, latest.Quality, latest.Temperature, latest.Humidity, latest.At, Anchor));
            Assert.Equal(scenario.Bpm == 0 ? "poor" : "good", latest.Quality);
            Assert.True(latest.HasPhoto);
        }
    }

    [Fact]
    public void MonthExceedsOnePageAndIncludesGapsAndUnreliableSignals()
    {
        foreach (var scenario in DemoSeries.Scenarios)
        {
            var samples = DemoSeries.Samples(Anchor).Where(x => x.Pin == scenario.Pin).OrderBy(x => x.At).ToList();
            Assert.Equal(125, samples.Count);
            Assert.True(samples.Count > 100);
            Assert.True(samples.Count(x => x.At >= Anchor.AddDays(-7)) < samples.Count / 2);
            Assert.All(samples, x => Assert.True(x.At <= Anchor));
            Assert.Equal(TimeSpan.FromDays(31), samples[^1].At - samples[0].At);
            Assert.All(samples.Zip(samples.Skip(1), (a, b) => b.At - a.At), gap => Assert.Equal(TimeSpan.FromHours(6), gap));
            Assert.Contains(samples, x => x.Quality == "fair");
            Assert.Contains(samples, x => x.Quality == "poor");
            Assert.Contains(samples, x => x.Bpm == 0 && !string.IsNullOrWhiteSpace(x.Reason));
            Assert.Equal(3, samples.Count(x => x.HasPhoto));
            Assert.All(samples, x =>
            {
                Assert.InRange(x.Bpm, 0, 250); Assert.InRange(x.Temperature, 0, 50); Assert.InRange(x.Humidity, 0, 100);
                Assert.True(Guid.TryParse(x.MeasurementClientId, out _)); Assert.True(Guid.TryParse(x.EnvironmentClientId, out _));
            });
        }
    }

    [Fact]
    public void IdentifiersAreStableAndRefreshMovesOnlyLatestTimestamps()
    {
        var first = DemoSeries.Samples(Anchor).ToList();
        var repeat = DemoSeries.Samples(Anchor).ToList();
        Assert.Equal(first, repeat);
        Assert.Equal(first.Count * 2, first.SelectMany(x => new[] { x.MeasurementClientId, x.EnvironmentClientId }).Distinct().Count());
        var refreshed = DemoSeries.Samples(Anchor.AddHours(1)).ToList();
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].MeasurementClientId, refreshed[i].MeasurementClientId);
            Assert.Equal(first[i].EnvironmentClientId, refreshed[i].EnvironmentClientId);
            Assert.Equal(first[i].At.AddHours(1), refreshed[i].At);
        }
    }
}
