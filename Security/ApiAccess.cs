using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitoringApi.Data;

namespace MonitoringApi.Security;

public sealed class ApiAccess(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration config, ApiDbContext db, IPasswordHasher<DeviceRegistration> hasher)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[7..].Trim();
            if (token.Length is < 32 or > 512) return AuthenticateResult.Fail("Invalid session.");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var session = await db.DeviceSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccessHash == hash, Context.RequestAborted);
            if (session is null || session.AccessExpiresTicks <= DateTimeOffset.UtcNow.UtcTicks
                || !await db.CrewMembers.AnyAsync(x => x.Pin == session.Pin && !x.Retired)
                || !await db.Devices.AnyAsync(x => x.DeviceId == session.DeviceId && x.Active && x.Kind == "uno"))
                return AuthenticateResult.Fail("Invalid session.");
            if (Request.Headers.ContainsKey("X-Device-Id"))
            {
                if (Request.Headers["X-Device-Id"] != session.DeviceId) return AuthenticateResult.Fail("Invalid device.");
                var registered = await db.Devices.SingleAsync(x => x.DeviceId == session.DeviceId);
                if (hasher.VerifyHashedPassword(registered, registered.CredentialHash, Request.Headers["X-Device-Key"].ToString()) == PasswordVerificationResult.Failed)
                    return AuthenticateResult.Fail("Invalid device.");
            }
            return Success([new("caller", "device"), new("device_id", session.DeviceId), new("device_kind", "uno"), new("pin", session.Pin), new("sid", session.Id)]);
        }
        var key = Request.Headers["X-Api-Key"].ToString();
        if (key.Length > 0)
        {
            var expected = config["MonitoringApi:WebApiKey"] ?? "";
            if (expected.Length < 32 || key.Length > 512 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
                return AuthenticateResult.Fail("Invalid API credential.");
            return Success([new Claim("caller", "web")]);
        }
        var id = Request.Headers["X-Device-Id"].ToString();
        var secret = Request.Headers["X-Device-Key"].ToString();
        if (id.Length == 0 && secret.Length == 0) return AuthenticateResult.NoResult();
        if (id.Length > 128 || secret.Length is < 16 or > 512) return AuthenticateResult.Fail("Invalid device credential.");
        // The existing camera sends its key without X-Device-Id. Resolve only the camera tied to this measurement.
        if (id.Length == 0 && Request.RouteValues["measurementId"] is string measurementId)
            id = await db.Measurements.Where(x => x.Id == measurementId).Select(x => x.CameraDeviceId).SingleOrDefaultAsync() ?? "";
        if (id.Length == 0) return AuthenticateResult.Fail("Invalid device credential.");
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == id && x.Active, Context.RequestAborted);
        if (device is null || string.IsNullOrEmpty(device.CredentialHash) ||
            hasher.VerifyHashedPassword(device, device.CredentialHash, secret) == PasswordVerificationResult.Failed)
            return AuthenticateResult.Fail("Invalid device credential.");
        return Success([new Claim("caller", "device"), new Claim("device_id", id), new Claim("device_kind", device.Kind)]);
    }
    private AuthenticateResult Success(Claim[] claims) => AuthenticateResult.Success(
        new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
}
