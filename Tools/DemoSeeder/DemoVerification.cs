using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Astra.Demo;

public sealed record DemoReport(string Pin, string State, int Bpm, string Quality, double TemperatureC, double Humidity,
    DateTimeOffset PulseAt, DateTimeOffset CabinAt, int WeekPulse, int MonthPulse, int WeekCabin, int MonthCabin,
    int MonthPulsePages, int MonthCabinPages, string PhotoStatus, int VerifiedPhotos, string LatestJpegSha256,
    int FullPulse, int FullCabin, DateTimeOffset HistoryFrom, DateTimeOffset HistoryTo);

public static class DemoVerification
{
    public static async Task<List<DemoReport>> VerifyAsync(ApiDbContext db, DemoApi api, DemoState state, List<DemoSample> samples)
    {
        var reports = new List<DemoReport>();
        var to = DateTimeOffset.UtcNow;
        foreach (var scenario in DemoSeries.Scenarios)
        {
            var series = samples.Where(x => x.Pin == scenario.Pin).OrderBy(x => x.At).ToList();
            var full = await ReadAsync<HistoryPulse>(api, scenario.Pin, "measurements", series[0].At, series[^1].At);
            var fullCabin = await ReadAsync<EnvironmentReadingDto>(api, scenario.Pin, "environment-readings", series[0].At, series[^1].At);
            var fullPulseKeys = series.Select(x => x.MeasurementClientId).ToArray();
            var fullCabinKeys = series.Select(x => x.EnvironmentClientId).ToArray();
            var pulseRows = await db.Measurements.AsNoTracking().Where(x => x.Pin == scenario.Pin && fullPulseKeys.Contains(x.ClientMeasurementId)).ToListAsync();
            var cabinRows = await db.EnvironmentReadings.AsNoTracking().Where(x => x.Pin == scenario.Pin && fullCabinKeys.Contains(x.ClientReadingId)).ToListAsync();
            var pulseIds = pulseRows.Select(x => x.Id).ToHashSet();
            var cabinIds = cabinRows.Select(x => x.Id).ToHashSet();
            var pulseTimes = full.Items.Where(x => pulseIds.Contains(x.MeasurementId)).OrderBy(x => x.MeasuredAt).Select(x => x.MeasuredAt).ToArray();
            var cabinTimes = fullCabin.Items.Where(x => cabinIds.Contains(x.ReadingId)).OrderBy(x => x.MeasuredAt).Select(x => x.MeasuredAt).ToArray();
            if (pulseTimes.Length != DemoSeries.SampleCount || cabinTimes.Length != DemoSeries.SampleCount
                || !pulseTimes.SequenceEqual(series.Select(x => x.At)) || !cabinTimes.SequenceEqual(pulseTimes)
                || pulseTimes.Zip(pulseTimes.Skip(1), (a, b) => b - a).Any(gap => gap != TimeSpan.FromHours(6))
                || pulseTimes[^1] - pulseTimes[0] != TimeSpan.FromDays(31))
                throw new InvalidOperationException($"PIN {scenario.Pin} does not have 125 synchronized check-ins exactly six hours apart over 31 days.");
            var month = await ReadAsync<HistoryPulse>(api, scenario.Pin, "measurements", to.AddDays(-30), to);
            var week = await ReadAsync<HistoryPulse>(api, scenario.Pin, "measurements", to.AddDays(-7), to);
            var monthCabin = await ReadAsync<EnvironmentReadingDto>(api, scenario.Pin, "environment-readings", to.AddDays(-30), to);
            var weekCabin = await ReadAsync<EnvironmentReadingDto>(api, scenario.Pin, "environment-readings", to.AddDays(-7), to);
            if (month.Items.Count <= 100 || month.Pages < 2 || monthCabin.Items.Count <= 100 || monthCabin.Pages < 2)
                throw new InvalidOperationException("Month did not exercise cursor pagination; the persisted demo anchor may be too old.");
            if (week.Items.Count >= month.Items.Count || weekCabin.Items.Count >= monthCabin.Items.Count)
                throw new InvalidOperationException("Week and Month are not visibly different.");
            if (month.Items.Select(x => x.MeasurementId).Distinct().Count() != month.Items.Count
                || monthCabin.Items.Select(x => x.ReadingId).Distinct().Count() != monthCabin.Items.Count)
                throw new InvalidOperationException("A paginated response duplicated records.");
            var latest = month.Items.OrderByDescending(x => x.MeasuredAt).First();
            var cabin = monthCabin.Items.OrderByDescending(x => x.MeasuredAt).First();
            var expected = samples.Single(x => x.Pin == scenario.Pin && x.Slot == 0);
            if (latest.MeasuredAt != expected.At || cabin.MeasuredAt != expected.At
                || latest.Pulse.Bpm != scenario.Bpm || latest.Pulse.Quality != (scenario.Bpm == 0 ? "poor" : "good")
                || cabin.TemperatureC != scenario.Temperature || cabin.RelativeHumidityPercent != scenario.Humidity
                || latest.MeasuredAt > to || cabin.MeasuredAt > to)
                throw new InvalidOperationException($"Latest API readings for {scenario.Pin} differ from the intended scenario.");
            var severity = DemoSeries.Severity(latest.Pulse.Bpm, latest.Pulse.Quality, cabin.TemperatureC, cabin.RelativeHumidityPercent, latest.MeasuredAt, to);
            if (severity != scenario.State) throw new InvalidOperationException($"Severity boundary mismatch for {scenario.Pin}: {severity}.");
            var pulseClientIds = samples.Where(x => x.Pin == scenario.Pin && x.At >= to.AddDays(-30) && x.At <= to).Select(x => x.MeasurementClientId).ToArray();
            var expectedIds = await db.Measurements.AsNoTracking().Where(x => x.Pin == scenario.Pin && pulseClientIds.Contains(x.ClientMeasurementId)).Select(x => x.Id).ToListAsync();
            if (expectedIds.Count != pulseClientIds.Length || expectedIds.Except(month.Items.Select(x => x.MeasurementId)).Any())
                throw new InvalidOperationException("Pagination omitted seeded measurement records.");
            var cabinClientIds = samples.Where(x => x.Pin == scenario.Pin && x.At >= to.AddDays(-30) && x.At <= to).Select(x => x.EnvironmentClientId).ToArray();
            var expectedCabinIds = await db.EnvironmentReadings.AsNoTracking().Where(x => x.Pin == scenario.Pin && cabinClientIds.Contains(x.ClientReadingId)).Select(x => x.Id).ToListAsync();
            if (expectedCabinIds.Count != cabinClientIds.Length || expectedCabinIds.Except(monthCabin.Items.Select(x => x.ReadingId)).Any())
                throw new InvalidOperationException("Pagination omitted seeded cabin records.");
            var verified = 0; string? latestHash = null;
            foreach (var sample in samples.Where(x => x.Pin == scenario.Pin && x.HasPhoto))
            {
                var id = await db.Measurements.Where(x => x.Pin == scenario.Pin && x.ClientMeasurementId == sample.MeasurementClientId).Select(x => x.Id).SingleAsync();
                var metadata = await api.JsonAsync<MeasurementDto>(HttpMethod.Get, $"api/v1/crew/{scenario.Pin}/measurements/{id}");
                if (metadata.PhotoStatus != "uploaded" || metadata.PhotoUrl is null) throw new InvalidOperationException("A required photo is not uploaded.");
                var jpeg = await api.PhotoAsync(scenario.Pin, id);
                using var image = Image.Load<Rgb24>(jpeg);
                if (image.Width != 320 || image.Height != 240 || image.Metadata.DecodedImageFormat?.Name != "JPEG")
                    throw new InvalidOperationException("Authenticated photo GET did not decode as a 320x240 JPEG.");
                if (sample.Slot == 0)
                {
                    if (id != latest.MeasurementId || !await api.AnonymousPhotoIsDeniedAsync(scenario.Pin, id))
                        throw new InvalidOperationException("Latest portrait association or photo authentication failed.");
                    latestHash = Convert.ToHexString(SHA256.HashData(jpeg));
                }
                verified++;
            }
            var report = new DemoReport(scenario.Pin, severity, latest.Pulse.Bpm, latest.Pulse.Quality, cabin.TemperatureC, cabin.RelativeHumidityPercent,
                latest.MeasuredAt, cabin.MeasuredAt, week.Items.Count, month.Items.Count, weekCabin.Items.Count, monthCabin.Items.Count,
                month.Pages, monthCabin.Pages, "uploaded", verified, latestHash!, pulseTimes.Length, cabinTimes.Length, pulseTimes[0], pulseTimes[^1]);
            reports.Add(report);
            Console.WriteLine($"{report.Pin} | {report.State} | {report.Bpm} bpm | {report.TemperatureC:0.#} C | {report.Humidity:0.#}% | {report.PulseAt:yyyy-MM-dd HH:mm:ss} UTC | Week {report.WeekPulse}/{report.WeekCabin} Month {report.MonthPulse}/{report.MonthCabin} | {report.VerifiedPhotos} authenticated JPEGs OK");
            Console.WriteLine($"  Full history: {report.FullPulse} pulse / {report.FullCabin} cabin; exact 6-hour intervals; {report.HistoryFrom:yyyy-MM-dd HH:mm:ss} to {report.HistoryTo:yyyy-MM-dd HH:mm:ss} UTC (31 days).");
        }
        if (reports.Select(x => x.LatestJpegSha256).Distinct().Count() != DemoSeries.Scenarios.Length)
            throw new InvalidOperationException("Latest portraits are not distinct across PINs.");
        return reports;
    }

    private static async Task<(List<T> Items, int Pages)> ReadAsync<T>(DemoApi api, string pin, string endpoint, DateTimeOffset from, DateTimeOffset to)
    {
        var bounds = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&limit=100";
        var items = new List<T>(); var cursors = new HashSet<string>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await api.JsonAsync<ApiPage<T>>(HttpMethod.Get, $"api/v1/crew/{pin}/{endpoint}?{bounds}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            items.AddRange(page.Items); pages++; cursor = page.NextCursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("API repeated a pagination cursor.");
        } while (cursor is not null);
        return (items, pages);
    }
}
