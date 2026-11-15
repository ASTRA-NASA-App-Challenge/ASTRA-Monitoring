using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Services;

namespace MonitoringApi.Controllers;

// Bounded batching on the private Development seed host. Each item still goes through
// the normal creation handlers, including validation, device checks and idempotency.
[ApiController]
[Authorize(Policy = "web")]
[Route("api/v1/crew/{pin}/demo-history")]
public sealed class DemoHistoryController(ApiDbContext db, IWebHostEnvironment environment,
    IConfiguration configuration, SupabasePhotoStorage storage) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(512 * 1024)]
    public async Task<IActionResult> Create(string pin, DemoHistoryRequest body)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("DemoSeedHost")
            || HttpContext.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            return NotFound();
        if (!DemoSeedContract.Pins.Contains(pin) || body.Items is not { Count: > 0 and <= 125 })
            return UnprocessableEntity();
        if (body.Items.Any(x => x is null)) return UnprocessableEntity();
        var slots = Enumerable.Range(0, DemoSeedContract.SampleCount).Where(i => !DemoSeedContract.NeedsPhoto(i))
            .ToDictionary(i => DemoSeedContract.ClientId("pulse", pin, i));
        if (body.Items.Select(x => x.Measurement?.ClientMeasurementId).Distinct().Count() != body.Items.Count)
            return UnprocessableEntity();
        foreach (var item in body.Items)
        {
            if (item.Measurement is not { Pulse: { }, Photo: { } } pulse || item.Environment is not { } cabin
                || !slots.TryGetValue(pulse.ClientMeasurementId, out var slot)
                || cabin.ClientReadingId != DemoSeedContract.ClientId("cabin", pin, slot)
                || pulse.Pulse.AlgorithmVersion != DemoSeedContract.Algorithm
                || pulse.MeasuredAt == default || pulse.MeasuredAt != cabin.MeasuredAt || pulse.MeasuredAt > DateTimeOffset.UtcNow)
                return UnprocessableEntity();
            if (!await db.Devices.AnyAsync(x => x.DeviceId == pulse.Photo.CameraId && x.Kind == "camera" && x.Active && x.PairedDeviceId == cabin.DeviceId)
                || !await db.Devices.AnyAsync(x => x.DeviceId == cabin.DeviceId && x.Kind == "uno" && x.Active && x.PairedDeviceId == pulse.Photo.CameraId))
                return BadRequest();
        }
        var handler = new MonitoringController(db, environment, storage) { ControllerContext = ControllerContext };
        var measurementsCreated = 0;
        var environmentCreated = 0;
        foreach (var item in body.Items)
        {
            var pulse = await handler.CreateMeasurement(pin, item.Measurement);
            if (pulse is not ObjectResult { StatusCode: 200 or 201 } p) return pulse;
            if (p.StatusCode == 201) measurementsCreated++;
            var cabin = await handler.Environment(pin, item.Environment);
            if (cabin is not ObjectResult { StatusCode: 200 or 201 } c) return cabin;
            if (c.StatusCode == 201) environmentCreated++;
            db.ChangeTracker.Clear();
        }
        return Ok(new DemoHistoryResponse(body.Items.Count, measurementsCreated, environmentCreated));
    }
}

public sealed record DemoHistoryItem(CreateMeasurementRequest Measurement, EnvironmentRequest Environment);
public sealed record DemoHistoryRequest(List<DemoHistoryItem> Items);
public sealed record DemoHistoryResponse(int Processed, int MeasurementsCreated, int EnvironmentReadingsCreated);
