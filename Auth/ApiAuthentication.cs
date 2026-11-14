using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitoringApi.Data;
using MonitoringApi.Identity;

namespace MonitoringApi.Auth;

public static class ApiSchemes
{
    public const string Bearer = "ApiBearer";
    public const string Device = "ApiDevice";
}

public sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IServiceScopeFactory scopes,
    UserManager<ApplicationUser> users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers["Authorization"].ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = raw[7..].Trim();
        if (token.Length < 32 || token.Length > 512) return AuthenticateResult.Fail("Invalid bearer token.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var hash = TokenSecurity.Hash(token);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccessTokenHash == hash);
        if (session is null || session.RevokedAt is not null || session.AccessExpiresAt <= DateTimeOffset.UtcNow)
            return AuthenticateResult.Fail("Invalid or expired bearer token.");
        if (!await db.Devices.AnyAsync(x => x.DeviceId == session.UnoDeviceId && x.Kind == "uno" && x.Active))
            return AuthenticateResult.Fail("Invalid or expired bearer token.");
        var user = await users.FindByIdAsync(session.AstronautId);
        if (user is null) return AuthenticateResult.Fail("Invalid or expired bearer token.");
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, session.AstronautId),
            new Claim("sid", session.Id),
            new Claim("uno_device_id", session.UnoDeviceId),
            new Claim("auth_kind", "astronaut")
        };
        foreach (var role in await users.GetRolesAsync(user)) claims.Add(new Claim(ClaimTypes.Role, role));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IServiceScopeFactory scopes,
    IPasswordHasher<DeviceRegistration> hasher)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Device-Id"].ToString();
        var key = Request.Headers["X-Device-Key"].ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key)) return AuthenticateResult.NoResult();
        if (id.Length > 128 || key.Length > 1024) return AuthenticateResult.Fail("Invalid device credentials.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == id && x.Active);
        if (device is null || hasher.VerifyHashedPassword(device, device.CredentialHash, key) == PasswordVerificationResult.Failed)
            return AuthenticateResult.Fail("Invalid device credentials.");
        var claims = new[]
        {
            new Claim("device_id", device.DeviceId),
            new Claim("device_kind", device.Kind),
            new Claim(ClaimTypes.NameIdentifier, device.DeviceId),
            new Claim("paired_device_id", device.PairedDeviceId ?? string.Empty),
            new Claim("astronaut_ids", device.AstronautIdsJson)
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public static class TokenSecurity
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class ApiDeviceProvisioner(
    ApiDbContext db,
    IConfiguration configuration,
    IPasswordHasher<DeviceRegistration> hasher)
{
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var configuredDevices = configuration.GetSection("MonitoringApi:Devices").GetChildren().ToArray();
        var configuredIds = configuredDevices.Select(x => x["DeviceId"]?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet();
        if (configuredDevices.Length > 0)
            foreach (var inactive in await db.Devices.Where(x => x.Active).ToListAsync(cancellationToken))
                if (!configuredIds.Contains(inactive.DeviceId)) inactive.Active = false;

        foreach (var item in configuredDevices)
        {
            var id = item["DeviceId"]?.Trim();
            var secret = item["Credential"];
            var kind = item["Kind"]?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) || kind is not ("uno" or "camera"))
                throw new InvalidOperationException("Each MonitoringApi:Devices entry requires DeviceId, Credential, and Kind (uno or camera).");
            var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == id, cancellationToken);
            if (device is null)
            {
                device = new DeviceRegistration { DeviceId = id, Kind = kind, CredentialHash = "" };
                db.Devices.Add(device);
            }
            device.Kind = kind;
            device.CredentialHash = hasher.HashPassword(device, secret);
            device.PairedDeviceId = item["PairedDeviceId"];
            device.AstronautIdsJson = System.Text.Json.JsonSerializer.Serialize(item.GetSection("AstronautIds").Get<string[]>() ?? []);
            device.Active = true;
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var camera in db.Devices.Where(x => x.Kind == "camera" && x.PairedDeviceId != null))
            if (!await db.Devices.AnyAsync(x => x.DeviceId == camera.PairedDeviceId && x.Kind == "uno"
                && x.Active && x.PairedDeviceId == camera.DeviceId, cancellationToken))
                throw new InvalidOperationException($"Camera '{camera.DeviceId}' is paired to an unknown Uno.");
    }
}
