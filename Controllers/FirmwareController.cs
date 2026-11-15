using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Services;

namespace MonitoringApi.Controllers;

// Compatibility with the unchanged Uno and camera firmware; no web usernames are stored.
[ApiController, Authorize, Route("api/v1")]
public sealed class FirmwareController(ApiDbContext db, IConfiguration config, IWebHostEnvironment environment,
    SupabasePhotoStorage photos) : ControllerBase
{
    private string? DeviceId => User.FindFirst("device_id")?.Value;
    private string? SessionPin => User.FindFirst("pin")?.Value;
    private bool IsUno => User.HasClaim("caller", "device") && User.HasClaim("device_kind", "uno");
    private MonitoringController Monitoring => new(db, environment, photos) { ControllerContext = ControllerContext };

    [HttpGet("astronauts")]
    public async Task<IActionResult> Roster()
    {
        if (!IsUno) return Forbid();
        var pins = await db.CrewMembers.AsNoTracking().Where(x => !x.Retired).OrderBy(x => x.Pin).Select(x => x.Pin).ToListAsync();
        var items = pins.Take(8).Select(pin => new { astronaut_id = Handle(pin), username = Label(pin), display_name = Label(pin) });
        return Ok(new { items, next_cursor = (string?)null });
    }

    [HttpPost("auth/sessions"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(FirmwareLogin body)
    {
        if (!IsUno || DeviceId is null) return Forbid();
        if (!CrewMember.IsValid(body.Pin) || body.AstronautId != Handle(body.Pin)) return Unauthorized();
        await using var tx = await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        if (!await db.CrewMembers.AnyAsync(x => x.Pin == body.Pin && !x.Retired)) return Unauthorized();
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        await db.DeviceSessions.Where(x => x.RefreshExpiresTicks <= nowTicks).ExecuteDeleteAsync();
        var session = new DeviceSession { Id = $"ses_{Guid.NewGuid():N}", Pin = body.Pin, DeviceId = DeviceId, AccessHash = "", RefreshHash = "" };
        var response = Tokens(session);
        db.DeviceSessions.Add(session);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return StatusCode(201, response);
    }

    [HttpPost("auth/sessions/refresh"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Refresh(FirmwareRefresh body)
    {
        if (body.RefreshToken.Length is < 32 or > 512) return Unauthorized();
        await using var tx = await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        var hash = Hash(body.RefreshToken);
        var session = await db.DeviceSessions.SingleOrDefaultAsync(x => x.RefreshHash == hash);
        if (session is null || session.RefreshExpiresTicks <= DateTimeOffset.UtcNow.UtcTicks
            || !await db.CrewMembers.AnyAsync(x => x.Pin == session.Pin && !x.Retired)
            || !await db.Devices.AnyAsync(x => x.DeviceId == session.DeviceId && x.Active && x.Kind == "uno")) return Unauthorized();
        var response = Tokens(session);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(response);
    }

    [HttpDelete("auth/sessions/current")]
    public async Task<IActionResult> Logout()
    {
        var id = User.FindFirst("sid")?.Value;
        if (id is null) return Unauthorized();
        await db.DeviceSessions.Where(x => x.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpPost("measurements")]
    public async Task<IActionResult> Create(CreateMeasurementRequest body)
    {
        if (!IsUno || SessionPin is null) return Forbid();
        var result = await Monitoring.CreateMeasurement(SessionPin, body);
        // Firmware requires 201 even when a retry returns an existing record.
        return result is OkObjectResult replay ? StatusCode(201, replay.Value) : result;
    }

    [HttpPost("environment-readings")]
    public async Task<IActionResult> Environment(EnvironmentRequest body)
    {
        if (!IsUno || SessionPin is null) return Forbid();
        var result = await Monitoring.Environment(SessionPin, body);
        return result is OkObjectResult replay ? StatusCode(201, replay.Value) : result;
    }

    [HttpGet("measurements/{measurementId}")]
    public async Task<IActionResult> Status(string measurementId)
    {
        if (!IsUno || SessionPin is null) return Forbid();
        return await Monitoring.GetMeasurement(SessionPin, measurementId);
    }

    [HttpPut("measurements/{measurementId}/photo"), RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Photo(string measurementId)
    {
        if (!User.HasClaim("caller", "device") || !User.HasClaim("device_kind", "camera")) return Forbid();
        var pin = await db.Measurements.Where(x => x.Id == measurementId && x.CameraDeviceId == DeviceId).Select(x => x.Pin).SingleOrDefaultAsync();
        return pin is null ? NotFound() : await Monitoring.UploadPhoto(pin, measurementId);
    }

    private string Handle(string pin) => "crew_" + Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(config["MonitoringApi:WebApiKey"]!), Encoding.UTF8.GetBytes(pin)))[..16];
    private string Label(string pin) => "Crew " + Handle(pin)[5..11];
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static object Tokens(DeviceSession session)
    {
        var access = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        session.AccessHash = Hash(access); session.RefreshHash = Hash(refresh);
        session.AccessExpiresTicks = DateTimeOffset.UtcNow.AddMinutes(15).UtcTicks;
        session.RefreshExpiresTicks = DateTimeOffset.UtcNow.AddHours(8).UtcTicks;
        return new { access_token = access, token_type = "Bearer", expires_in = 900, refresh_token = refresh, refresh_expires_in = 28800 };
    }
}

public sealed record FirmwareLogin([Required] string AstronautId, [Required] string Pin);
public sealed record FirmwareRefresh([Required] string RefreshToken);
