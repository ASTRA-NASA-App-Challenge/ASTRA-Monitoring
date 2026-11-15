using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using MonitoringApi.Data;
using MonitoringApi.Services;

namespace MonitoringApi.Tests;

public class StartupDemoTests
{
    [Fact]
    public async Task DevelopmentStartupWaitsForWebRegistrationBeforePreparingData()
    {
        using var f = new Fixture();
        var runner = new Runner(async () => { await f.SeedAsync(); return true; });
        using var worker = new StartupDemoSeed(f.Provider.GetRequiredService<IServiceScopeFactory>(), new Lifetime(),
            new Environment { EnvironmentName = Environments.Development }, runner, NullLogger<StartupDemoSeed>.Instance);
        await worker.StartAsync(default);
        await Task.Delay(100);
        Assert.Equal(0, runner.Calls);
        await using (var check = f.Provider.CreateAsyncScope())
            Assert.Empty(await check.ServiceProvider.GetRequiredService<ApiDbContext>().CrewMembers.ToListAsync());
        await f.RegisterAsync();
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, runner.Calls);
        await using var scope = f.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        Assert.Equal(DemoSeedContract.Pins.OrderBy(x => x), await db.CrewMembers.OrderBy(x => x.Pin).Select(x => x.Pin).ToListAsync());
        Assert.Equal(DemoSeedReadiness.Complete, await DemoSeedInventory.CheckAsync(db, default));
        Assert.Equal(DemoSeedContract.Pins.Count, await db.CrewMembers.CountAsync());
    }

    [Fact]
    public async Task InventoryWaitsForExistingPinsAndNeverRecreatesRetiredPins()
    {
        using var f = new Fixture();
        await using var scope = f.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        Assert.Equal(DemoSeedReadiness.WaitingForPins, await DemoSeedInventory.CheckAsync(db, default));
        await f.RegisterAsync();
        Assert.Equal(DemoSeedReadiness.NeedsPreparation, await DemoSeedInventory.CheckAsync(db, default));
        await db.CrewMembers.Where(x => x.Pin == "410201").ExecuteUpdateAsync(s => s.SetProperty(x => x.Retired, true));
        var runner = new Runner(() => throw new Exception("A retired PIN must not be prepared."));
        await f.RunAsync(runner);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(DemoSeedReadiness.RetiredPin, await DemoSeedInventory.CheckAsync(db, default));
    }

    [Fact]
    public async Task MissingDataRunsPreparationOnceAndRestartKeepsTimestamps()
    {
        using var f = new Fixture(); await f.RegisterAsync();
        var runner = new Runner(async () => { await f.SeedAsync(); return true; });
        await f.RunAsync(runner); Assert.Equal(1, runner.Calls);
        await using var scope = f.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var before = await db.Measurements.OrderBy(x => x.Id).Select(x => x.MeasuredAt).ToListAsync();
        var restart = new Runner(() => throw new Exception("A complete dataset must be reused."));
        await f.RunAsync(restart);
        Assert.Equal(0, restart.Calls);
        Assert.Equal(before, await db.Measurements.OrderBy(x => x.Id).Select(x => x.MeasuredAt).ToListAsync());
        Assert.Equal(750, await db.Measurements.CountAsync());
    }

    [Fact]
    public async Task FailedVerificationIsRetriedEvenWhenAllRowsExist()
    {
        using var f = new Fixture(); await f.RegisterAsync(); await f.SeedAsync();
        var progress = new DemoSeedProgress(); progress.Set("failed");
        var runner = new Runner(() => { progress.Set("verifying"); return Task.FromResult(true); });
        using var worker = new StartupDemoSeed(f.Provider.GetRequiredService<IServiceScopeFactory>(), new Lifetime(),
            new Environment { EnvironmentName = Environments.Development }, runner, NullLogger<StartupDemoSeed>.Instance, progress);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, runner.Calls);
        Assert.Equal("complete", progress.Phase);
    }

    [Fact]
    public async Task MissingCheckInOrPhotoMetadataMakesDatasetIncomplete()
    {
        using var f = new Fixture(); await f.RegisterAsync(); await f.SeedAsync();
        await using var scope = f.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        Assert.Equal(DemoSeedReadiness.Complete, await DemoSeedInventory.CheckAsync(db, default));
        var key = DemoSeedContract.ClientId("pulse", "520301", 0);
        await db.Measurements.Where(x => x.ClientMeasurementId == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.PhotoStatus, "awaiting_upload"));
        Assert.Equal(DemoSeedReadiness.NeedsPreparation, await DemoSeedInventory.CheckAsync(db, default));
        await db.Measurements.Where(x => x.ClientMeasurementId == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.PhotoStatus, "uploaded"));
        await db.EnvironmentReadings.Where(x => x.ClientReadingId == DemoSeedContract.ClientId("cabin", "410201", 124)).ExecuteDeleteAsync();
        Assert.Equal(DemoSeedReadiness.NeedsPreparation, await DemoSeedInventory.CheckAsync(db, default));
    }

    private sealed class Runner(Func<Task<bool>> run) : IStartupDemoPreparation
    {
        public int Calls;
        public Task<bool> PrepareAsync(string root, string connection, CancellationToken ct) { Calls++; return run(); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
        public ServiceProvider Provider { get; }
        public Fixture()
        {
            connection.Open();
            Provider = new ServiceCollection().AddDbContext<ApiDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
            using var scope = Provider.CreateScope(); scope.ServiceProvider.GetRequiredService<ApiDbContext>().Database.EnsureCreated();
        }
        public async Task RegisterAsync()
        {
            await using var scope = Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            db.CrewMembers.AddRange(DemoSeedContract.Pins.Select(pin => new CrewMember { Pin = pin })); await db.SaveChangesAsync();
        }
        public async Task SeedAsync()
        {
            await using var scope = Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            foreach (var pin in DemoSeedContract.Pins)
            for (var i = 0; i < DemoSeedContract.SampleCount; i++)
            {
                var id = DemoSeedContract.ClientId("pulse", pin, i); var at = DateTimeOffset.UtcNow.AddHours(-6 * i);
                var photo = DemoSeedContract.NeedsPhoto(i);
                db.Measurements.Add(new Measurement { Id = id, Pin = pin, ClientMeasurementId = id, UnoDeviceId = "test", CameraDeviceId = "test",
                    MeasuredAt = at, CreatedAt = at, Bpm = 72, Quality = "good", AlgorithmVersion = DemoSeedContract.Algorithm,
                    MediaType = "image/jpeg", PhotoStatus = photo ? "uploaded" : "expired", PhotoPath = photo ? "test-path" : null, PhotoSha256 = photo ? "test-hash" : null });
                var env = DemoSeedContract.ClientId("cabin", pin, i);
                db.EnvironmentReadings.Add(new EnvironmentReading { Id = env, Pin = pin, ClientReadingId = env, DeviceId = "test", MeasuredAt = at, ReceivedAt = at });
            }
            await db.SaveChangesAsync();
        }
        public async Task RunAsync(Runner runner)
        {
            using var worker = new StartupDemoSeed(Provider.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), new Environment { EnvironmentName = Environments.Development }, runner, NullLogger<StartupDemoSeed>.Instance);
            await worker.StartAsync(default); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public void Dispose() { Provider.Dispose(); connection.Dispose(); }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Environment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
