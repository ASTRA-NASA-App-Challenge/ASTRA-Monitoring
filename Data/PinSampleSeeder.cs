using Microsoft.EntityFrameworkCore;

namespace MonitoringApi.Data;

public static class PinSampleSeeder
{
    public static async Task SeedAsync(ApiDbContext db, string pin)
    {
        if (!CrewMember.IsValid(pin)) throw new ArgumentException("Seed PIN must contain exactly six digits.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await db.CrewMembers.AnyAsync(x => x.Pin == pin))
            db.CrewMembers.Add(new CrewMember { Pin = pin });

        var now = DateTimeOffset.UtcNow;
        var bpms = new[] { 68, 72, 70, 75, 69, 71, 73 };
        for (var i = 0; i < bpms.Length; i++)
        {
            var measuredAt = now.AddDays(-i).AddMinutes(-10);
            var measurementId = $"mea_sample_{pin}_{i}";
            if (!await db.Measurements.AnyAsync(x => x.Id == measurementId))
                db.Measurements.Add(new Measurement
                {
                    Id = measurementId, Pin = pin, ClientMeasurementId = $"sample-{pin}-{i}",
                    UnoDeviceId = "sample-uno", CameraDeviceId = "sample-camera",
                    MeasuredAt = measuredAt, CreatedAt = measuredAt.AddSeconds(1),
                    Bpm = bpms[i], Quality = "good", AlgorithmVersion = "synthetic-sample-v1",
                    MediaType = "image/jpeg", Width = 160, Height = 120,
                    PhotoStatus = "expired", PhotoExpiresAt = measuredAt.AddMinutes(5)
                });
            var readingId = $"env_sample_{pin}_{i}";
            if (!await db.EnvironmentReadings.AnyAsync(x => x.Id == readingId))
                db.EnvironmentReadings.Add(new EnvironmentReading
                {
                    Id = readingId, Pin = pin, ClientReadingId = $"sample-{pin}-{i}",
                    DeviceId = "sample-uno", MeasuredAt = measuredAt, ReceivedAt = measuredAt.AddSeconds(1),
                    TemperatureC = 21.5 + i * 0.2, RelativeHumidityPercent = 43 + i
                });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        var measurements = await db.Measurements.CountAsync(x => x.Pin == pin && x.AlgorithmVersion == "synthetic-sample-v1");
        var readings = await db.EnvironmentReadings.CountAsync(x => x.Pin == pin && x.DeviceId == "sample-uno");
        Console.WriteLine($"Sample data persisted for PIN {pin}: {measurements} measurements, {readings} environment readings. No photos uploaded.");
    }
}
