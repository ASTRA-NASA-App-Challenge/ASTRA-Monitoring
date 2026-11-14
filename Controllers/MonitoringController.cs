using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MonitoringApi.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1")]
public sealed class MonitoringController(ApiDbContext db, IWebHostEnvironment environment, SupabasePhotoStorage photoStorage) : ControllerBase
{
    private const long MaxPhotoBytes = 5 * 1024 * 1024;
    private static readonly string[] Dimensions = ["160x120", "320x240"];

    // --- No-auth replacements for claims ---
    private string AstronautId => Request.Headers["X-Astronaut-Id"].FirstOrDefault() is { Length: > 0 } h ? h : "dev-astronaut";
    private string SessionId   => Request.Headers["X-Session-Id"].FirstOrDefault()   is { Length: > 0 } h ? h : "dev-session";

    [HttpPost("measurements")]
    public async Task<IActionResult> CreateMeasurement([FromBody] CreateMeasurementRequest body)
    {
        if (!Guid.TryParse(body.ClientMeasurementId, out _) || body.MeasuredAt.Offset != TimeSpan.Zero
            || body.Pulse.Bpm is < 0 or > 250 || body.Pulse.Quality is not ("good" or "fair" or "poor" or "unknown")
            || string.IsNullOrWhiteSpace(body.Pulse.AlgorithmVersion) || body.Pulse.Bpm == 0 && string.IsNullOrWhiteSpace(body.Pulse.StatusReason)
            || !ValidWaveform(body.Pulse.Waveform)
            || !Dimensions.Contains($"{body.Photo.Width}x{body.Photo.Height}")
            || body.Photo.MediaType is not ("image/jpeg" or "application/vnd.demo.rgb565"))
            return UnprocessableEntity();

        var camera = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == body.Photo.CameraId && x.Kind == "camera");
        if (camera is null) return NotFound("Unknown camera device");
        var unoId = camera.PairedDeviceId;

        var now = DateTimeOffset.UtcNow;
        var item = new Measurement
        {
            Id = $"mea_{Guid.NewGuid():N}", ClientMeasurementId = body.ClientMeasurementId,
            AstronautId = AstronautId, SessionId = SessionId,
            UnoDeviceId = unoId, CameraDeviceId = camera.DeviceId, MeasuredAt = body.MeasuredAt, CreatedAt = now,
            Bpm = body.Pulse.Bpm, Quality = body.Pulse.Quality, AlgorithmVersion = body.Pulse.AlgorithmVersion,
            PulseStatusReason = body.Pulse.StatusReason,
            WaveformJson = body.Pulse.Waveform is null ? null : JsonSerializer.Serialize(body.Pulse.Waveform),
            MediaType = body.Photo.MediaType, Width = body.Photo.Width, Height = body.Photo.Height,
            PhotoExpiresAt = now.AddMinutes(5)
        };
        db.Measurements.Add(item);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return StatusCode(201, CreatedMeasurement(item));
    }

    [HttpGet("measurements/{measurementId}")]
    public async Task<IActionResult> GetMeasurement(string measurementId)
    {
        var item = await db.Measurements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == measurementId);
        if (item is null) return NotFound();
        if (item.PhotoStatus == "awaiting_upload" && item.PhotoExpiresAt <= DateTimeOffset.UtcNow)
        {
            var expired = await db.Measurements.SingleAsync(x => x.Id == measurementId);
            expired.PhotoStatus = "expired";
            await db.SaveChangesAsync(HttpContext.RequestAborted);
            item.PhotoStatus = "expired";
        }
        return Ok(ToMeasurement(item));
    }

    [HttpPut("measurements/{measurementId}/photo")]
    [RequestSizeLimit(MaxPhotoBytes)]
    public async Task<IActionResult> UploadPhoto(string measurementId)
    {
        var item = await db.Measurements.SingleOrDefaultAsync(x => x.Id == measurementId);
        if (item is null) return NotFound();
        if (!string.Equals(Request.ContentType?.Split(';')[0].Trim(), item.MediaType, StringComparison.OrdinalIgnoreCase))
            return StatusCode(415);
        if (item.PhotoStatus == "uploaded")
        {
            var digest = await HashRequestBody();
            return item.PhotoSha256 == digest
                ? Ok(new PhotoResponse(item.Id, "uploaded", item.PhotoReceivedAt ?? DateTimeOffset.UtcNow))
                : Conflict();
        }
        if (item.PhotoStatus != "awaiting_upload" || item.PhotoExpiresAt <= DateTimeOffset.UtcNow) return StatusCode(410);

        var tempPath = Path.Combine(Path.GetTempPath(), $"astra-{Guid.NewGuid():N}.tmp");
        var normalizedPath = Path.Combine(Path.GetTempPath(), $"astra-{Guid.NewGuid():N}.jpg");
        try
        {
            var (length, digest) = await SaveBoundedAsync(Request.Body, tempPath, MaxPhotoBytes, HttpContext.RequestAborted);
            if (item.MediaType == "application/vnd.demo.rgb565" && length != item.Width * item.Height * 2L)
                return UnprocessableEntity();

            if (item.MediaType == "image/jpeg")
            {
                try
                {
                    var info = await Image.IdentifyAsync(tempPath, HttpContext.RequestAborted);
                    if (info is null || info.Width != item.Width || info.Height != item.Height) return UnprocessableEntity();
                    using var image = await Image.LoadAsync<Rgb24>(tempPath, HttpContext.RequestAborted);
                    await image.SaveAsJpegAsync(normalizedPath, new JpegEncoder { Quality = 85 }, HttpContext.RequestAborted);
                }
                catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("SixLabors.ImageSharp", StringComparison.Ordinal) == true)
                {
                    return UnprocessableEntity();
                }
            }
            else
            {
                var pixels = await DecodeRgb565Async(tempPath, item.Width, item.Height, HttpContext.RequestAborted);
                using var image = Image.LoadPixelData(pixels, item.Width, item.Height);
                await image.SaveAsJpegAsync(normalizedPath, new JpegEncoder { Quality = 85 }, HttpContext.RequestAborted);
            }

            await using var uploadStream = System.IO.File.OpenRead(normalizedPath);
            var publicUrl = await photoStorage.UploadAsync(uploadStream, $"{item.Id}.jpg", "image/jpeg", HttpContext.RequestAborted);

            item.PhotoPath = publicUrl;
            item.PhotoSha256 = digest;
            item.PhotoStatus = "uploaded";
            item.PhotoReceivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(HttpContext.RequestAborted);
            return Ok(new PhotoResponse(item.Id, item.PhotoStatus, item.PhotoReceivedAt.Value));
        }
        catch (PayloadTooLargeException) { return StatusCode(413); }
        catch (Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException) { return StatusCode(413); }
        finally
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
            if (System.IO.File.Exists(normalizedPath)) System.IO.File.Delete(normalizedPath);
        }
    }

    [HttpGet("astronauts/{astronautId}/measurements")]
    public async Task<IActionResult> History(string astronautId, [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null, [FromQuery] int limit = 50, [FromQuery] string? cursor = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-31);
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || end < start || end - start > TimeSpan.FromDays(31))
            return UnprocessableEntity();
        var query = db.Measurements.AsNoTracking().Where(x => x.AstronautId == astronautId && x.MeasuredAt >= start && x.MeasuredAt <= end);
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var ticks, out var id)) return BadRequest();
            var time = new DateTimeOffset(ticks, TimeSpan.Zero);
            query = query.Where(x => x.MeasuredAt < time || (x.MeasuredAt == time && string.Compare(x.Id, id) > 0));
        }
        var page = await query.OrderByDescending(x => x.MeasuredAt).ThenBy(x => x.Id).Take(limit + 1).ToListAsync();
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore && page.Count > 0 ? EncodeCursor(page[^1].MeasuredAt, page[^1].Id) : null;
        var items = page.Select(x => new HistoryMeasurementDto(x.Id, x.MeasuredAt, new PulseSummary(x.Bpm, x.Quality, x.PulseStatusReason),
            x.PhotoStatus == "uploaded" ? $"/api/v1/measurements/{x.Id}/photo" : null)).ToArray();
        return Ok(new PageDto<HistoryMeasurementDto>(items, next));
    }

    [HttpGet("measurements/{measurementId}/photo")]
    public async Task<IActionResult> GetPhoto(string measurementId)
    {
        var item = await db.Measurements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == measurementId);
        if (item is null) return NotFound();
        if (item.PhotoStatus != "uploaded" || item.PhotoPath is null || string.IsNullOrWhiteSpace(item.PhotoPath)) return NotFound();
        Response.Headers["Cache-Control"] = "private, no-store";
        return Redirect(item.PhotoPath);
    }

    [HttpPost("environment-readings")]
    public async Task<IActionResult> Environment([FromBody] EnvironmentRequest body)
    {
        var unoId = body.DeviceId;
        if (!Guid.TryParse(body.ClientReadingId, out _) || body.Sensor != "DHT11" || body.MeasuredAt.Offset != TimeSpan.Zero || !double.IsFinite(body.TemperatureC)
            || !double.IsFinite(body.RelativeHumidityPercent) || body.TemperatureC is < 0 or > 50
            || body.RelativeHumidityPercent is < 0 or > 100) return UnprocessableEntity();
        var sessionId = SessionId;
        var reading = new EnvironmentReading
        {
            Id = $"env_{Guid.NewGuid():N}", ClientReadingId = body.ClientReadingId,
            SessionId = sessionId, AstronautId = AstronautId, DeviceId = unoId,
            MeasuredAt = body.MeasuredAt, ReceivedAt = DateTimeOffset.UtcNow, TemperatureC = body.TemperatureC,
            RelativeHumidityPercent = body.RelativeHumidityPercent
        };
        db.EnvironmentReadings.Add(reading);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return StatusCode(201, EnvironmentResponseFor(reading));
    }

    private CreateMeasurementResponse CreatedMeasurement(Measurement x) => new(x.Id, x.ClientMeasurementId, x.AstronautId,
        x.CreatedAt, x.PhotoStatus, x.PhotoExpiresAt);
    private static MeasurementDto ToMeasurement(Measurement x) => new(x.Id, x.MeasuredAt, new PulseSummary(x.Bpm, x.Quality, x.PulseStatusReason),
        x.PhotoStatus, x.PhotoStatus == "uploaded" ? $"/api/v1/measurements/{x.Id}/photo" : null);
    private static bool ValidWaveform(WaveformDto? x) => x is null ||
        (x.SampleRateHz is > 0 and <= 200 && x.AdcResolutionBits is > 0 and <= 16 && x.Samples.Length <= 4000
         && x.Samples.Length <= x.SampleRateHz * 20 && x.Samples.All(n => n >= 0 && n < (1 << x.AdcResolutionBits)));
    private static bool MeasurementMatches(Measurement x, CreateMeasurementRequest body) =>
        x.MeasuredAt == body.MeasuredAt && x.Bpm == body.Pulse.Bpm && x.Quality == body.Pulse.Quality
        && x.AlgorithmVersion == body.Pulse.AlgorithmVersion && x.MediaType == body.Photo.MediaType
        && x.CameraDeviceId == body.Photo.CameraId
        && x.PulseStatusReason == body.Pulse.StatusReason
        && x.Width == body.Photo.Width && x.Height == body.Photo.Height
        && x.WaveformJson == (body.Pulse.Waveform is null ? null : JsonSerializer.Serialize(body.Pulse.Waveform));
    private static bool ReadingMatches(EnvironmentReading x, EnvironmentRequest b) => x.ClientReadingId == b.ClientReadingId
        && x.MeasuredAt == b.MeasuredAt && x.DeviceId == b.DeviceId && x.TemperatureC == b.TemperatureC
        && x.RelativeHumidityPercent == b.RelativeHumidityPercent;
    private static EnvironmentResponse EnvironmentResponseFor(EnvironmentReading x) =>
        new(x.Id, x.ClientReadingId, x.AstronautId, x.ReceivedAt, "accepted");
    private async Task<string> HashRequestBody()
    {
        using var sha = SHA256.Create();
        await using var stream = Request.Body;
        var hash = await sha.ComputeHashAsync(stream, HttpContext.RequestAborted);
        return Convert.ToHexString(hash);
    }
    private static async Task<(long Length, string Hash)> SaveBoundedAsync(Stream input, string path, long maxBytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new PayloadTooLargeException();
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (total, Convert.ToHexString(sha.GetHashAndReset()));
    }
    private static async Task<Rgb24[]> DecodeRgb565Async(string path, int width, int height, CancellationToken cancellationToken)
    {
        var bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
        var pixels = new Rgb24[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var value = (bytes[i * 2] << 8) | bytes[i * 2 + 1];
            var red = (value >> 11) & 0x1f;
            var green = (value >> 5) & 0x3f;
            var blue = value & 0x1f;
            pixels[i] = new Rgb24((byte)((red * 255 + 15) / 31), (byte)((green * 255 + 31) / 63), (byte)((blue * 255 + 15) / 31));
        }
        return pixels;
    }
    private static string EncodeCursor(DateTimeOffset time, string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{time.UtcTicks}:{id}"));
    private static bool TryDecodeCursor(string cursor, out long ticks, out string id)
    {
        ticks = 0; id = "";
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var split = value.IndexOf(':');
            return split > 0 && long.TryParse(value[..split], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
                && (id = value[(split + 1)..]).Length > 0;
        }
        catch { return false; }
    }
}

public sealed record HistoryMeasurementDto(string MeasurementId, DateTimeOffset MeasuredAt, PulseSummary Pulse, string? PhotoUrl);
public sealed class PayloadTooLargeException : Exception { }