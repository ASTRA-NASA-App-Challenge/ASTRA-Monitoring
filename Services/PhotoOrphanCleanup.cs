using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace MonitoringApi.Services;

public sealed class PhotoOrphanCleanup(ApiDbContext db, SupabasePhotoStorage photos, ILogger<PhotoOrphanCleanup> logger)
{
    public async Task ProcessAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var expired = await db.PhotoUploadLeases.AsNoTracking().Where(x => x.ExpiresTicks <= now
            && db.CrewMembers.Any(c => c.Pin == x.Pin && !c.Retired)).Take(100).ToListAsync(ct);
        foreach (var lease in expired)
        {
            // Claim with a fresh token so an old request cannot resume while cleanup runs.
            var claim = Guid.NewGuid().ToString("N");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2).UtcTicks;
            var claimed = await db.PhotoUploadLeases.Where(x => x.MeasurementId == lease.MeasurementId && x.Token == lease.Token && x.ExpiresTicks <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Token, claim).SetProperty(x => x.ExpiresTicks, deadline), ct);
            if (claimed != 1) continue;
            try
            {
                // Metadata may have committed even if the client never received the response.
                if (!await db.Measurements.AnyAsync(x => x.Id == lease.MeasurementId && x.PhotoStatus == "uploaded", ct))
                    await photos.DeleteObjectAsync(lease.Pin, lease.MeasurementId, ct);
                await db.PhotoUploadLeases.Where(x => x.MeasurementId == lease.MeasurementId && x.Token == claim).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                await db.PhotoUploadLeases.Where(x => x.MeasurementId == lease.MeasurementId && x.Token == claim)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresTicks, 0L), ct);
                logger.LogWarning("An interrupted photo upload still needs cleanup; retrying later.");
            }
        }
    }
}
