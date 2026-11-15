using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Services;

namespace MonitoringApi.Controllers;

[ApiController]
[Authorize(Policy = "web")]
[Route("api/v1/demo/status")]
public sealed class DemoStatusController(ApiDbContext db, IWebHostEnvironment environment,
    IConfiguration configuration, DemoSeedProgress progress) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!environment.IsDevelopment() || configuration.GetValue<bool>("DemoSeedHost")) return NotFound();
        Response.Headers.CacheControl = "no-store";
        var readiness = await DemoSeedInventory.CheckAsync(db, ct);
        var pins = DemoSeedContract.Pins.ToArray();
        var pulseKeys = pins.SelectMany(pin => Enumerable.Range(0, DemoSeedContract.SampleCount)
            .Select(i => DemoSeedContract.ClientId("pulse", pin, i))).ToArray();
        var cabinKeys = pins.SelectMany(pin => Enumerable.Range(0, DemoSeedContract.SampleCount)
            .Select(i => DemoSeedContract.ClientId("cabin", pin, i))).ToArray();
        var pulses = await db.Measurements.AsNoTracking().Where(x => pins.Contains(x.Pin)
            && x.AlgorithmVersion == DemoSeedContract.Algorithm && pulseKeys.Contains(x.ClientMeasurementId))
            .Select(x => new { x.Pin, x.ClientMeasurementId, x.PhotoStatus }).ToListAsync(ct);
        var photos = pulses.Count(x => Enumerable.Range(0, DemoSeedContract.SampleCount).Where(DemoSeedContract.NeedsPhoto)
            .Any(i => DemoSeedContract.ClientId("pulse", x.Pin, i) == x.ClientMeasurementId) && x.PhotoStatus == "uploaded");
        var enabled = configuration.GetValue("Demo:SeedOnStartup", true);
        var state = readiness switch
        {
            DemoSeedReadiness.RetiredPin => "skipped",
            DemoSeedReadiness.WaitingForPins => enabled ? "waiting_for_pins" : "disabled",
            DemoSeedReadiness.Complete when progress.Phase == "failed" => "retrying",
            DemoSeedReadiness.Complete when progress.Phase is "preparing" or "verifying" => "verifying",
            DemoSeedReadiness.Complete => "complete",
            _ when !enabled => "disabled",
            _ when progress.Phase == "failed" => "retrying",
            _ => "preparing"
        };
        return Ok(new { state, registered_pins = await db.CrewMembers.CountAsync(x => pins.Contains(x.Pin) && !x.Retired && !x.DeletionPending, ct),
            expected_pins = pins.Length, measurements = pulses.Count,
            environment_readings = await db.EnvironmentReadings.CountAsync(x => pins.Contains(x.Pin) && cabinKeys.Contains(x.ClientReadingId), ct),
            uploaded_photos = photos, expected_measurements = pulseKeys.Length,
            expected_environment_readings = cabinKeys.Length, expected_photos = pins.Length * 3 });
    }
}
