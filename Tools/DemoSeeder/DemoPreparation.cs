using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Controllers;

namespace Astra.Demo;

public static class DemoPreparation
{
    public static async Task PrepareAsync(ApiDbContext db, DemoApi api, DemoStorage storage, string root, DemoState state, List<DemoSample> samples)
    {
        var pulseIds = samples.Select(x => x.MeasurementClientId).ToArray();
        var cabinIds = samples.Select(x => x.EnvironmentClientId).ToArray();
        var pulses = await db.Measurements.AsNoTracking().Where(x => pulseIds.Contains(x.ClientMeasurementId)).ToDictionaryAsync(x => x.ClientMeasurementId);
        var cabin = await db.EnvironmentReadings.AsNoTracking().Where(x => cabinIds.Contains(x.ClientReadingId)).ToDictionaryAsync(x => x.ClientReadingId);
        var assets = new Dictionary<string, byte[]>();
        foreach (var pin in DemoSeries.Scenarios.Select(x => x.Pin)) assets[pin] = await File.ReadAllBytesAsync(Path.Combine(root, "Tools", "DemoSeeder", "Assets", pin + ".jpg"));
        // Populate every member's recent history first. A bounded request amortizes HTTP
        // overhead while the server uses the same creation handlers for every record.
        foreach (var group in samples.Where(x => !x.HasPhoto).GroupBy(x => x.Pin))
        {
            var missing = group.Where(x => !pulses.ContainsKey(x.MeasurementClientId) || !cabin.ContainsKey(x.EnvironmentClientId)).ToList();
            if (missing.Count == 0) continue;
            var items = missing.Select(sample => new DemoHistoryItem(
                new CreateMeasurementRequest(sample.MeasurementClientId, sample.At,
                    new PulseDto(sample.Bpm, sample.Quality, DemoSeries.Algorithm, null, sample.Reason),
                    new PhotoExpectationDto(state.CameraId!, "image/jpeg", 320, 240)),
                new EnvironmentRequest(sample.EnvironmentClientId, sample.At, state.UnoId!, "DHT11", sample.Temperature, sample.Humidity))).ToList();
            var result = await api.JsonAsync<DemoHistoryResponse>(HttpMethod.Post, $"api/v1/crew/{group.Key}/demo-history", new DemoHistoryRequest(items));
            Console.WriteLine($"{group.Key}: history ready; {result.MeasurementsCreated} pulse and {result.EnvironmentReadingsCreated} cabin readings added.");
        }
        foreach (var group in samples.Where(x => x.HasPhoto).OrderBy(x => x.Slot).ThenBy(x => x.Pin).GroupBy(x => new { x.Pin, x.HasPhoto }))
        {
            var created = 0; var reused = 0;
            foreach (var sample in group)
            {
                pulses.TryGetValue(sample.MeasurementClientId, out var existing);
                if (existing is not null && (existing.Pin != sample.Pin || existing.AlgorithmVersion != DemoSeries.Algorithm))
                    throw new InvalidOperationException("A stable demo UUID conflicts with a non-demo record; nothing will be overwritten.");
                if (existing is not null && sample.HasPhoto && existing.PhotoStatus != "uploaded" && existing.PhotoExpiresAt <= DateTimeOffset.UtcNow)
                {
                    var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                    if (await db.PhotoUploadLeases.AnyAsync(x => x.MeasurementId == existing.Id && x.ExpiresTicks > nowTicks))
                        throw new InvalidOperationException("An incomplete demo photo still has a live upload lease; wait for it to finish before resuming.");
                    // Replace only an unfinished demo attempt. Never extend expiry or claim an upload succeeded.
                    await storage.RemoveIncompleteObjectAsync(sample.Pin, existing.Id);
                    await using var repair = await db.Database.BeginTransactionAsync();
                    await db.PhotoUploadLeases.Where(x => x.MeasurementId == existing.Id && x.ExpiresTicks <= nowTicks).ExecuteDeleteAsync();
                    var deleted = await db.Measurements.Where(x => x.Id == existing.Id && x.Pin == sample.Pin && x.AlgorithmVersion == DemoSeries.Algorithm
                        && x.ClientMeasurementId == sample.MeasurementClientId && x.PhotoStatus != "uploaded").ExecuteDeleteAsync();
                    if (deleted != 1) throw new InvalidOperationException("Incomplete demo attempt changed while preparing its replacement.");
                    await repair.CommitAsync(); existing = null;
                }
                if (existing is null || sample.Slot == 0)
                {
                    var request = new CreateMeasurementRequest(sample.MeasurementClientId, sample.At,
                        new PulseDto(sample.Bpm, sample.Quality, DemoSeries.Algorithm, null, sample.Reason),
                        new PhotoExpectationDto(state.CameraId!, "image/jpeg", 320, 240));
                    var response = await api.JsonAsync<CreateMeasurementResponse>(HttpMethod.Post, $"api/v1/crew/{sample.Pin}/measurements", request);
                    if (existing is null) { created++; existing = await db.Measurements.AsNoTracking().SingleAsync(x => x.Id == response.MeasurementId); }
                    else reused++;
                }
                else reused++;
                if (sample.HasPhoto)
                {
                    if (existing!.PhotoStatus == "uploaded")
                    {
                        if (existing.PhotoSha256 != state.AssetHashes[sample.Pin]) throw new InvalidOperationException("Stored demo upload hash differs from its original portrait asset.");
                    }
                    else
                    {
                        await api.UploadAsync(sample.Pin, existing!.Id, assets[sample.Pin]);
                        Console.WriteLine($"{sample.Pin}: portrait uploaded through photo PUT ({(sample.Slot == 0 ? "latest" : "historical")}).");
                    }
                }
                if (!cabin.ContainsKey(sample.EnvironmentClientId) || sample.Slot == 0)
                {
                    var request = new EnvironmentRequest(sample.EnvironmentClientId, sample.At, state.UnoId!, "DHT11", sample.Temperature, sample.Humidity);
                    await api.JsonAsync<EnvironmentResponse>(HttpMethod.Post, $"api/v1/crew/{sample.Pin}/environment-readings", request);
                }
                if ((created + reused) % 20 == 0) Console.WriteLine($"{sample.Pin}: processed {created + reused} {(group.Key.HasPhoto ? "photo" : "history")} samples.");
            }
            Console.WriteLine($"{group.Key.Pin}: {created} created, {reused} reused ({(group.Key.HasPhoto ? "photo samples" : "history")}).");
        }
    }
}
