using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MonitoringApi.Data;

namespace Astra.Demo;

public static class DemoRunner
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length != 1 || args[0] is not ("inspect" or "prepare" or "refresh" or "verify" or "import-portraits"))
        {
            Console.WriteLine("Usage: dotnet run --project Tools/DemoSeeder -- inspect|prepare|refresh|verify");
            return 2;
        }
        try
        {
            var root = FindRoot();
            if (args[0] == "import-portraits") { DemoAssets.Import(root); return 0; }
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(root).AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", true)
                .AddUserSecrets(typeof(ApiDbContext).Assembly, true).AddEnvironmentVariables();
            DotEnvConfiguration.Add(configuration, root, []);
            var sqlite = new SqliteConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("SQLite connection missing.")) { ForeignKeys = true };
            if (!Path.IsPathRooted(sqlite.DataSource)) sqlite.DataSource = Path.Combine(root, sqlite.DataSource);
            if (!File.Exists(sqlite.DataSource)) throw new InvalidOperationException("The configured SQLite file does not exist; no replacement database will be created.");
            var options = new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(sqlite.ToString()).Options;
            await using var db = new ApiDbContext(options);
            var pins = DemoSeries.Scenarios.Select(x => x.Pin).ToArray();
            foreach (var pin in pins)
            {
                var crew = await db.CrewMembers.AsNoTracking().SingleOrDefaultAsync(x => x.Pin == pin);
                if (crew is null || crew.Retired || crew.DeletionPending) throw new InvalidOperationException($"PIN {pin} must already be registered and active; the seeder never registers or restores identities.");
                if (args[0] == "inspect")
                {
                    var latest = await db.Measurements.Where(x => x.Pin == pin).OrderByDescending(x => x.MeasuredAt).FirstOrDefaultAsync();
                    Console.WriteLine($"{pin}: active; {await db.Measurements.CountAsync(x => x.Pin == pin)} measurements, {await db.EnvironmentReadings.CountAsync(x => x.Pin == pin)} cabin readings; latest {latest?.MeasuredAt.ToString("O") ?? "none"}.");
                }
            }
            if (args[0] == "inspect")
            {
                Console.WriteLine($"Active devices: {await db.Devices.CountAsync(x => x.Active)}. SQLite: {sqlite.DataSource}"); return 0;
            }
            var key = configuration["MonitoringApi:WebApiKey"];
            if (key is not { Length: >= 32 and <= 512 }) throw new InvalidOperationException("Configured web API key is missing or invalid.");
            var store = Path.Combine(root, ".demo", "presentation-v1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sqlite.DataSource))))[..16]);
            Directory.CreateDirectory(store);
            await using var runLock = new FileStream(Path.Combine(store, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var statePath = Path.Combine(store, "state.json");
            var existed = File.Exists(statePath);
            if (!existed && args[0] != "prepare") throw new InvalidOperationException("Run prepare before refresh or verify.");
            if (!existed && await db.Measurements.AnyAsync(x => x.AlgorithmVersion == DemoSeries.Algorithm))
                throw new InvalidOperationException("Demo rows already exist but their persisted anchor is missing. Restore .demo/presentation-v1 rather than generating duplicate history.");
            var state = DemoState.Load(statePath);
            if (state.Version != 1) throw new InvalidOperationException("Unsupported demo state version.");
            if (state.CadenceVersion < 2 && args[0] is "refresh" or "verify")
                throw new InvalidOperationException("Run prepare once to upgrade the demo to a complete six-hour timeline before refreshing or verifying it.");
            var samples = DemoSeries.Samples(state.Anchor, state.Presentation).ToList();
            foreach (var pin in pins)
            {
                var asset = Path.Combine(root, "Tools", "DemoSeeder", "Assets", pin + ".jpg");
                if (!File.Exists(asset)) throw new InvalidOperationException($"Missing synthetic JPEG asset for {pin}.");
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(asset)));
                if (state.AssetHashes.TryGetValue(pin, out var previous) && previous != hash)
                    throw new InvalidOperationException("A demo portrait changed. Restore the original asset to preserve photo idempotency.");
                state.AssetHashes[pin] = hash;
            }
            await EnsureNoConflictingLatestAsync(db, state);
            var before = await FingerprintAsync(db, samples, state);
            var storage = new DemoStorage(configuration);
            await storage.VerifyPrivateAsync();
            if (args[0] != "verify") await EnsurePairAsync(db, state, statePath);
            if (state.UnoId is null || state.CameraId is null) throw new InvalidOperationException("The persisted demo pair is missing.");
            state.Save(statePath);
            await using var host = await DemoHost.StartAsync(root, sqlite.ToString());
            using var api = new DemoApi(host.Address, key);
            if (args[0] == "prepare")
            {
                await AlignAsync(db, state, state.Presentation, false);
                await DemoPreparation.PrepareAsync(db, api, storage, root, state, samples);
            }
            if (args[0] == "refresh" || args[0] == "prepare" && (!state.Prepared || state.CadenceVersion < 2))
            {
                await RefreshAsync(db, state);
                state.Prepared = true;
                state.CadenceVersion = 2;
                state.Save(statePath);
            }
            samples = DemoSeries.Samples(state.Anchor, state.Presentation).ToList();
            var report = await DemoVerification.VerifyAsync(db, api, state, samples);
            var after = await FingerprintAsync(db, samples, state);
            if (before != after) throw new InvalidOperationException("Non-demo data changed during this run. No non-demo row is intentionally modified; check for concurrent application writes.");
            await File.WriteAllTextAsync(Path.Combine(store, "report.json"), JsonSerializer.Serialize(report, Json));
            Console.WriteLine("Non-demo data and all identities preserved. Portraits retrieved as authenticated JPEGs. Report: " + Path.Combine(store, "report.json"));
            return 0;
        }
        catch (Exception ex)
        {
            // Deliberately omit raw configuration values, credentials, and HTTP headers.
            Console.Error.WriteLine($"Demo command failed ({ex.GetType().Name}): {ex.Message}"); return 1;
        }
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ASTRA-Monitoring.csproj"))) return d.FullName;
        throw new InvalidOperationException("Run from the ASTRA-Monitoring checkout.");
    }

    private static async Task EnsureNoConflictingLatestAsync(ApiDbContext db, DemoState state)
    {
        foreach (var scenario in DemoSeries.Scenarios)
        {
            var planned = DateTimeOffset.UtcNow.AddMinutes(-scenario.AgeMinutes);
            if (await db.Measurements.AnyAsync(x => x.Pin == scenario.Pin && x.AlgorithmVersion != DemoSeries.Algorithm && x.MeasuredAt >= planned))
                throw new InvalidOperationException($"PIN {scenario.Pin} has a newer non-demo pulse reading; preserving it conflicts with the requested demo latest scenario.");
            var ids = DemoSeries.Samples(state.Anchor, state.Presentation).Where(x => x.Pin == scenario.Pin).Select(x => x.EnvironmentClientId).ToArray();
            if (await db.EnvironmentReadings.AnyAsync(x => x.Pin == scenario.Pin && !ids.Contains(x.ClientReadingId) && x.MeasuredAt >= planned))
                throw new InvalidOperationException($"PIN {scenario.Pin} has a newer non-demo cabin reading; preserving it conflicts with the requested demo latest scenario.");
        }
    }

    private static async Task EnsurePairAsync(ApiDbContext db, DemoState state, string statePath)
    {
        if (state.UnoId is not null && state.CameraId is not null)
        {
            if (state.OwnsPair)
            {
                var owned = await db.Devices.Where(x => x.DeviceId == state.UnoId || x.DeviceId == state.CameraId).ToListAsync();
                if (owned.Count != 2 || state.UnoKey is null || state.CameraKey is null) throw new InvalidOperationException("Owned demo pair is incomplete; installed credentials will not be overwritten.");
                var ownedHasher = new PasswordHasher<DeviceRegistration>();
                foreach (var device in owned)
                {
                    var secret = device.DeviceId == state.UnoId ? state.UnoKey : state.CameraKey;
                    if (ownedHasher.VerifyHashedPassword(device, device.CredentialHash, secret) == PasswordVerificationResult.Failed)
                        throw new InvalidOperationException("Owned demo credentials changed; refusing to overwrite them.");
                    device.Active = true;
                }
                await db.SaveChangesAsync();
            }
            if (!await db.Devices.AnyAsync(x => x.DeviceId == state.UnoId && x.Kind == "uno" && x.Active && x.PairedDeviceId == state.CameraId)
                || !await db.Devices.AnyAsync(x => x.DeviceId == state.CameraId && x.Kind == "camera" && x.Active && x.PairedDeviceId == state.UnoId))
                throw new InvalidOperationException("The saved demo device pair is no longer active; no installed credentials will be overwritten.");
            return;
        }
        var pair = await (from camera in db.Devices.AsNoTracking() join uno in db.Devices.AsNoTracking() on camera.PairedDeviceId equals uno.DeviceId
            where camera.Active && uno.Active && camera.Kind == "camera" && uno.Kind == "uno" && uno.PairedDeviceId == camera.DeviceId
            select new { Camera = camera.DeviceId, Uno = uno.DeviceId }).FirstOrDefaultAsync();
        if (pair is not null) { state.UnoId = pair.Uno; state.CameraId = pair.Camera; return; }
        state.UnoId = "astra-presentation-uno-v1"; state.CameraId = "astra-presentation-camera-v1";
        if (await db.Devices.AnyAsync(x => x.DeviceId == state.UnoId || x.DeviceId == state.CameraId))
            throw new InvalidOperationException("Isolated demo device IDs already exist without a valid saved pair; refusing to overwrite credentials.");
        state.OwnsPair = true;
        state.UnoKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        state.CameraKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        state.Save(statePath);
        var unoDevice = new DeviceRegistration { DeviceId = state.UnoId, Kind = "uno", PairedDeviceId = state.CameraId, Active = true, CredentialHash = "" };
        var cameraDevice = new DeviceRegistration { DeviceId = state.CameraId, Kind = "camera", PairedDeviceId = state.UnoId, Active = true, CredentialHash = "" };
        var hasher = new PasswordHasher<DeviceRegistration>();
        unoDevice.CredentialHash = hasher.HashPassword(unoDevice, state.UnoKey);
        cameraDevice.CredentialHash = hasher.HashPassword(cameraDevice, state.CameraKey);
        db.Devices.AddRange(unoDevice, cameraDevice); await db.SaveChangesAsync();
        Console.WriteLine("Configured an isolated hashed-credential demo Uno/camera pair; installed device credentials are unchanged.");
    }

    private static async Task RefreshAsync(ApiDbContext db, DemoState state)
    {
        var now = DateTimeOffset.UtcNow;
        await AlignAsync(db, state, now, true);
        state.Presentation = now;
        Console.WriteLine("Refreshed all demo timestamps together, preserving exact six-hour intervals, identifiers, values, photos and identities.");
    }

    private static async Task AlignAsync(ApiDbContext db, DemoState state, DateTimeOffset presentation, bool requireComplete)
    {
        var samples = DemoSeries.Samples(state.Anchor, presentation).ToList();
        var pulseIds = samples.Select(x => x.MeasurementClientId).ToArray();
        var cabinIds = samples.Select(x => x.EnvironmentClientId).ToArray();
        await using var tx = await db.Database.BeginTransactionAsync();
        var pulses = await db.Measurements.Where(x => pulseIds.Contains(x.ClientMeasurementId) && x.AlgorithmVersion == DemoSeries.Algorithm).ToDictionaryAsync(x => x.ClientMeasurementId);
        var cabins = await db.EnvironmentReadings.Where(x => cabinIds.Contains(x.ClientReadingId) && x.DeviceId == state.UnoId).ToDictionaryAsync(x => x.ClientReadingId);
        if (requireComplete && (pulses.Count != samples.Count || cabins.Count != samples.Count))
            throw new InvalidOperationException("Demo rows are missing; run prepare to complete the six-hour history.");
        foreach (var sample in samples)
        {
            if (pulses.TryGetValue(sample.MeasurementClientId, out var pulse))
            { if (pulse.Pin != sample.Pin) throw new InvalidOperationException("Unexpected demo identity."); pulse.MeasuredAt = sample.At; }
            if (cabins.TryGetValue(sample.EnvironmentClientId, out var cabin))
            { if (cabin.Pin != sample.Pin) throw new InvalidOperationException("Unexpected demo identity."); cabin.MeasuredAt = sample.At; }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<string> FingerprintAsync(ApiDbContext db, List<DemoSample> samples, DemoState state)
    {
        var pulseKeys = samples.Select(x => x.MeasurementClientId).ToHashSet();
        var cabinKeys = samples.Select(x => x.EnvironmentClientId).ToHashSet();
        var crew = await db.CrewMembers.AsNoTracking().OrderBy(x => x.Pin).ToListAsync();
        var devices = (await db.Devices.AsNoTracking().OrderBy(x => x.DeviceId).ToListAsync())
            .Where(x => !state.OwnsPair || x.DeviceId != state.UnoId && x.DeviceId != state.CameraId);
        var measurements = (await db.Measurements.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Where(x => !pulseKeys.Contains(x.ClientMeasurementId));
        var environment = (await db.EnvironmentReadings.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Where(x => !cabinKeys.Contains(x.ClientReadingId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { crew, devices, measurements, environment }, Json))));
    }
}

public sealed class DemoHost : IAsyncDisposable
{
    private readonly Process process;
    public Uri Address { get; }
    private DemoHost(Process process, Uri address) { this.process = process; Address = address; }
    public static async Task<DemoHost> StartAsync(string root, string connection)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "DemoSeeder.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "DemoSeeder.deps.json"), typeof(ApiDbContext).Assembly.Location, "--urls", address.ToString(), "--DemoSeedHost=true", "--ConnectionStrings:DefaultConnection", connection, "--Logging:LogLevel:Default=Warning" }) start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the private seed API host.");
        // Consume output without forwarding potential application details or configuration values.
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("The private demo API host exited before becoming ready.");
            try { if ((await client.GetAsync(new Uri(address, "api/v1/healthz"))).IsSuccessStatusCode) return new(process, address); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(200);
        }
        process.Kill(true); process.Dispose(); throw new TimeoutException("Private demo API host did not become ready.");
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        process.Dispose();
    }
}
