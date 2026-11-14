using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Auth;
using MonitoringApi.Identity;

namespace MonitoringApi.Data;

public sealed class DemoDataSeeder(
    ApplicationDbContext identityDb,
    ApiDbContext apiDb,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    IPasswordHasher<DeviceRegistration> deviceHasher)
{
    private static readonly string[] UserNames = ["demo.rivera", "demo.chen"];

    public async Task<string> SeedAsync(string contentRoot)
    {
        if (await users.Users.AnyAsync(x => UserNames.Contains(x.UserName!))
            || await apiDb.Devices.AnyAsync() || await apiDb.Sessions.AnyAsync()
            || await apiDb.Measurements.AnyAsync() || await apiDb.EnvironmentReadings.AnyAsync())
            throw new InvalidOperationException("Demo seeding requires an empty monitoring database and no existing demo astronaut accounts.");

        var credentialFile = Path.Combine(contentRoot, ".demo-credentials.json");
        if (File.Exists(credentialFile))
            throw new InvalidOperationException(".demo-credentials.json already exists; remove it before creating a new demo dataset.");

        foreach (var role in AppRoles.All)
            if (!await roles.RoleExistsAsync(role))
                Ensure(await roles.CreateAsync(new IdentityRole(role)));

        var pins = UserNames.ToDictionary(x => x, _ => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"));
        var unoKey = TokenSecurity.NewToken();
        var cameraKey = TokenSecurity.NewToken();
        var astronauts = new List<ApplicationUser>();
        await using (var transaction = await identityDb.Database.BeginTransactionAsync())
        {
            for (var i = 0; i < UserNames.Length; i++)
            {
                var username = UserNames[i];
                var user = new ApplicationUser
                {
                    UserName = username,
                    Email = $"{username}@example.test",
                    EmailConfirmed = true,
                    IsGroundControl = false,
                    LockoutEnabled = true
                };
                Ensure(await users.CreateAsync(user, pins[username]));
                Ensure(await users.AddToRoleAsync(user, AppRoles.Crew));
                astronauts.Add(user);
            }
            await transaction.CommitAsync();
        }

        var credentialDocument = new
        {
            warning = "Local demo credentials. Never use these accounts or keys outside this demo.",
            astronauts = astronauts.Select(user => new { astronaut_id = user.Id, username = user.UserName, pin = pins[user.UserName!] }),
            devices = new[]
            {
                new { device_id = "uno-demo-01", device_key = unoKey },
                new { device_id = "cam-demo-01", device_key = cameraKey }
            }
        };
        await File.WriteAllTextAsync(credentialFile, JsonSerializer.Serialize(credentialDocument,
            new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(credentialFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var now = DateTimeOffset.UtcNow;
        var uno = new DeviceRegistration
        {
            DeviceId = "uno-demo-01", Kind = "uno", CredentialHash = "",
            PairedDeviceId = "cam-demo-01",
            AstronautIdsJson = JsonSerializer.Serialize(astronauts.Select(x => x.Id).ToArray())
        };
        var camera = new DeviceRegistration
        {
            DeviceId = "cam-demo-01", Kind = "camera", CredentialHash = "",
            PairedDeviceId = "uno-demo-01"
        };
        uno.CredentialHash = deviceHasher.HashPassword(uno, unoKey);
        camera.CredentialHash = deviceHasher.HashPassword(camera, cameraKey);
        apiDb.Devices.AddRange(uno, camera);

        foreach (var astronaut in astronauts)
        {
            var sessionId = $"ses_seed_{Guid.NewGuid():N}";
            var old = now.AddDays(-8);
            apiDb.Sessions.Add(new ApiSession
            {
                Id = sessionId,
                AstronautId = astronaut.Id,
                UnoDeviceId = uno.DeviceId,
                AccessTokenHash = TokenSecurity.Hash(TokenSecurity.NewToken()),
                AccessExpiresAt = old,
                RefreshTokenHash = TokenSecurity.Hash(TokenSecurity.NewToken()),
                RefreshExpiresAt = old,
                CreatedAt = old.AddHours(-8),
                RevokedAt = old
            });

            var sampleBpms = new[] { 68, 72, 70, 75, 69 };
            for (var i = 0; i < sampleBpms.Length; i++)
            {
                var measured = now.AddDays(-(i + 1)).AddHours(-i);
                apiDb.Measurements.Add(new Measurement
                {
                    Id = $"mea_seed_{Guid.NewGuid():N}",
                    ClientMeasurementId = Guid.NewGuid().ToString(),
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    AstronautId = astronaut.Id,
                    SessionId = sessionId,
                    UnoDeviceId = uno.DeviceId,
                    CameraDeviceId = camera.DeviceId,
                    MeasuredAt = measured,
                    CreatedAt = measured.AddSeconds(2),
                    Bpm = sampleBpms[(i + (astronaut == astronauts[1] ? 1 : 0)) % sampleBpms.Length],
                    Quality = i == 3 ? "fair" : "good",
                    AlgorithmVersion = "synthetic-demo-1.0",
                    MediaType = "image/jpeg",
                    Width = 160,
                    Height = 120,
                    PhotoStatus = "expired",
                    PhotoExpiresAt = measured.AddMinutes(5)
                });
            }

            for (var i = 0; i < 10; i++)
            {
                var measured = now.AddMinutes(-5).AddSeconds(-30 * i);
                apiDb.EnvironmentReadings.Add(new EnvironmentReading
                {
                    Id = $"env_seed_{Guid.NewGuid():N}",
                    ClientReadingId = Guid.NewGuid().ToString(),
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    SessionId = sessionId,
                    AstronautId = astronaut.Id,
                    DeviceId = uno.DeviceId,
                    MeasuredAt = measured,
                    ReceivedAt = measured.AddSeconds(1),
                    TemperatureC = 21.5 + i * 0.1,
                    RelativeHumidityPercent = 44 + i % 4
                });
            }
        }
        await apiDb.SaveChangesAsync();
        return credentialFile;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Demo Identity setup failed: " + string.Join(", ", result.Errors.Select(x => x.Code)));
    }
}
