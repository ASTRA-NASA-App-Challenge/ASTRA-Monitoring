using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace MonitoringApi.Services;

public enum DemoSeedReadiness { WaitingForPins, RetiredPin, NeedsPreparation, Complete }

public static class DemoSeedInventory
{
    public static async Task<DemoSeedReadiness> CheckAsync(ApiDbContext db, CancellationToken ct)
    {
        var pins = DemoSeedContract.Pins.ToArray();
        var crew = await db.CrewMembers.AsNoTracking().Where(x => pins.Contains(x.Pin)).ToListAsync(ct);
        if (crew.Any(x => x.Retired || x.DeletionPending)) return DemoSeedReadiness.RetiredPin;
        if (crew.Count != pins.Length) return DemoSeedReadiness.WaitingForPins;
        var pulse = await db.Measurements.AsNoTracking().Where(x => pins.Contains(x.Pin) && x.AlgorithmVersion == DemoSeedContract.Algorithm)
            .Select(x => new { x.Pin, x.ClientMeasurementId, x.PhotoStatus, x.PhotoPath, x.PhotoSha256 }).ToDictionaryAsync(x => x.ClientMeasurementId, ct);
        var keys = pins.SelectMany(pin => Enumerable.Range(0, DemoSeedContract.SampleCount).Select(i => DemoSeedContract.ClientId("cabin", pin, i))).ToArray();
        var cabin = await db.EnvironmentReadings.AsNoTracking().Where(x => pins.Contains(x.Pin) && keys.Contains(x.ClientReadingId))
            .Select(x => new { x.Pin, x.ClientReadingId }).ToDictionaryAsync(x => x.ClientReadingId, ct);
        foreach (var pin in pins)
        for (var i = 0; i < DemoSeedContract.SampleCount; i++)
        {
            if (!pulse.TryGetValue(DemoSeedContract.ClientId("pulse", pin, i), out var p) || p.Pin != pin
                || !cabin.TryGetValue(DemoSeedContract.ClientId("cabin", pin, i), out var c) || c.Pin != pin)
                return DemoSeedReadiness.NeedsPreparation;
            if (DemoSeedContract.NeedsPhoto(i) && (p.PhotoStatus != "uploaded" || string.IsNullOrEmpty(p.PhotoPath) || string.IsNullOrEmpty(p.PhotoSha256)))
                return DemoSeedReadiness.NeedsPreparation;
        }
        return DemoSeedReadiness.Complete;
    }
}

public interface IStartupDemoPreparation
{
    Task<bool> PrepareAsync(string root, string connection, CancellationToken ct);
}

public sealed class DemoSeedProcess(
    IConfiguration configuration,
    ILogger<DemoSeedProcess> logger,
    DemoSeedProgress? progress = null) : IStartupDemoPreparation
{
    public async Task<bool> PrepareAsync(string root, string connection, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(root, "Tools", "DemoSeeder", "DemoSeeder.csproj")))
        {
            logger.LogWarning("Automatic demo preparation needs the Tools/DemoSeeder source project and .NET SDK. Existing data is unchanged.");
            return false;
        }
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "--project", "Tools/DemoSeeder", "--artifacts-path", Path.Combine(root, "bin", "startup-demo-build"), "--verbosity", "quiet", "--", "prepare" }) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__DefaultConnection"] = connection;
        foreach (var key in new[] { "MonitoringApi:WebApiKey", "Supabase:Url", "Supabase:Bucket", "Supabase:ServiceRoleKey" })
            if (configuration[key] is { } value) start.Environment[key.Replace(":", "__")] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start automatic demo preparation.");
        // The tool prints progress only; do not forward stderr or credentials into host logs.
        var output = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("Verifying demo", StringComparison.Ordinal)) progress?.Set("verifying");
                logger.LogInformation("Demo preparation: {Progress}", line);
            }
        }, ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); await output; await error; }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
        if (process.ExitCode != 0) logger.LogWarning("Demo preparation exited with code {ExitCode}; existing identities and non-demo data are preserved. Run the prepare command for details.", process.ExitCode);
        return process.ExitCode == 0;
    }
}

public sealed class StartupDemoSeed(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    IWebHostEnvironment environment,
    IStartupDemoPreparation preparation,
    ILogger<StartupDemoSeed> logger,
    DemoSeedProgress? progress = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        var waitingLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
                var readiness = await DemoSeedInventory.CheckAsync(db, stoppingToken);
                if (readiness == DemoSeedReadiness.Complete && progress?.Phase != "failed")
                { progress?.Set("complete"); logger.LogInformation("Demo dataset is already complete; no readings, photos or timestamps changed."); return; }
                if (readiness == DemoSeedReadiness.RetiredPin)
                { progress?.Set("skipped"); logger.LogInformation("Demo preparation skipped because a demo PIN is retired; identities will not be recreated."); return; }
                if (readiness == DemoSeedReadiness.WaitingForPins)
                {
                    progress?.Set("waiting_for_pins");
                    if (!waitingLogged) logger.LogInformation("Demo preparation is waiting for the six existing demo PINs to be registered by the web app.");
                    waitingLogged = true;
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); continue;
                }
                logger.LogInformation("Demo data is missing or incomplete; preparing it automatically in the background.");
                progress?.Set("preparing");
                if (await preparation.PrepareAsync(environment.ContentRootPath, db.Database.GetDbConnection().ConnectionString, stoppingToken)) continue;
                progress?.Set("failed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            { progress?.Set("failed"); logger.LogWarning("Automatic demo preparation could not complete ({Failure}); it will retry. The API remains available.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
