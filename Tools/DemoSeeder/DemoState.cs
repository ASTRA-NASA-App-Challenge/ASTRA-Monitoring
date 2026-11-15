using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace Astra.Demo;

public sealed class DemoState
{
    public DateTimeOffset Presentation { get; set; }
    public string? UnoId { get; set; }
    public string? CameraId { get; set; }
    public bool OwnsPair { get; set; }
    public bool IsNew { get; set; }
    public Dictionary<string, string> AssetHashes { get; set; } = [];

    // Per-run context only. Resume from actual readings, without a saved time anchor.
    public static async Task<DemoState> FromDatabaseAsync(ApiDbContext db)
    {
        var rows = await db.Measurements.AsNoTracking().Where(x => x.AlgorithmVersion == DemoSeries.Algorithm)
            .ToDictionaryAsync(x => x.ClientMeasurementId);
        var samples = DemoSeries.Samples(DateTimeOffset.UtcNow).OrderBy(x => x.Slot).ToList();
        var cabinKeys = samples.Select(x => x.EnvironmentClientId).ToArray();
        var cabins = await db.EnvironmentReadings.AsNoTracking().Where(x => cabinKeys.Contains(x.ClientReadingId))
            .ToDictionaryAsync(x => x.ClientReadingId);
        foreach (var sample in samples)
        {
            var scenario = DemoSeries.Scenarios.Single(x => x.Pin == sample.Pin);
            if (rows.TryGetValue(sample.MeasurementClientId, out var row) && row.Pin == sample.Pin)
                return new DemoState { Presentation = row.MeasuredAt.AddMinutes(scenario.AgeMinutes).AddHours(6 * sample.Slot),
                    UnoId = row.UnoDeviceId, CameraId = row.CameraDeviceId };
            if (cabins.TryGetValue(sample.EnvironmentClientId, out var cabin) && cabin.Pin == sample.Pin)
                return new DemoState { Presentation = cabin.MeasuredAt.AddMinutes(scenario.AgeMinutes).AddHours(6 * sample.Slot) };
        }
        return new DemoState { Presentation = DateTimeOffset.UtcNow, IsNew = true };
    }
}
