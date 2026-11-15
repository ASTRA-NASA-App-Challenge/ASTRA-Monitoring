using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace MonitoringApi.Services;

public sealed class CrewDeletion(ApiDbContext db, SupabasePhotoStorage photos)
{
    public async Task RetireAsync(string pin, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A tombstone is also created when deletion arrives before a delayed registration.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CrewMembers (Pin, Retired, DeletionPending) VALUES ({pin}, 1, 1) ON CONFLICT(Pin) DO UPDATE SET Retired=1, DeletionPending=1", ct);
        await db.Measurements.Where(x => x.Pin == pin).ExecuteDeleteAsync(ct);
        await db.EnvironmentReadings.Where(x => x.Pin == pin).ExecuteDeleteAsync(ct);
        await db.DeviceSessions.Where(x => x.Pin == pin).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task PurgePhotosAsync(string pin, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (await db.PhotoUploadLeases.AnyAsync(x => x.Pin == pin && x.ExpiresTicks > now, ct))
            throw new InvalidOperationException("Photo cleanup will resume after the in-flight upload finishes.");
        await photos.DeleteFolderAsync(pin, ct);
        await db.PhotoUploadLeases.Where(x => x.Pin == pin).ExecuteDeleteAsync(ct);
        await db.CrewMembers.Where(x => x.Pin == pin && x.Retired).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletionPending, false), ct);
    }
}

public sealed class PhotoCleanupWorker(IServiceScopeFactory scopes, ILogger<PhotoCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
                var deletion = scope.ServiceProvider.GetRequiredService<CrewDeletion>();
                var pins = await db.CrewMembers.Where(x => x.Retired && x.DeletionPending).Select(x => x.Pin).ToListAsync(stoppingToken);
                foreach (var pin in pins)
                    try { await deletion.PurgePhotosAsync(pin, stoppingToken); }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
                    { logger.LogWarning("Photo cleanup will be retried."); }
                await scope.ServiceProvider.GetRequiredService<PhotoOrphanCleanup>().ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Microsoft.Data.Sqlite.SqliteException) { logger.LogWarning("Photo cleanup database is busy; retrying."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
