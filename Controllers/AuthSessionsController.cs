using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Auth;
using MonitoringApi.Data;
using MonitoringApi.Models;
using MonitoringApi.Identity;

namespace MonitoringApi.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class AuthSessionsController(
    ApiDbContext db,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn) : ControllerBase
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromHours(8);

    [HttpGet("healthz")]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new { status = "ok", api_version = "v1", time = DateTime.UtcNow });

    [HttpGet("astronauts")]
    [AllowAnonymous]
    [EnableRateLimiting("device-read")]
    public async Task<IActionResult> Astronauts([FromQuery] bool active = true, [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null, [FromQuery] string? query = null)
    {
        limit = Math.Clamp(limit, 1, 100);

        var candidates = new List<ApplicationUser>();
        foreach (var user in await users.Users.ToListAsync())
        {
            var roles = await users.GetRolesAsync(user);
            if (!roles.Contains(AppRoles.Crew) && !roles.Contains(AppRoles.Medic)) continue;
            if (!active || !user.LockoutEnd.HasValue || user.LockoutEnd <= DateTimeOffset.UtcNow) candidates.Add(user);
        }

        if (!string.IsNullOrWhiteSpace(query))
            candidates = candidates.Where(u => (u.UserName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        candidates = candidates.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();

        if (cursor is not null)
        {
            try
            {
                cursor = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (cursor.Length == 0) return BadRequest();
            }
            catch (FormatException) { return BadRequest(); }
            candidates = candidates.Where(u => string.CompareOrdinal(u.Id, cursor) > 0).ToList();
        }

        var page = candidates.Take(limit + 1).ToArray();
        var items = page.Take(limit).Select(ToAstronaut).ToArray();
        var next = page.Length > limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes(page[limit - 1].Id)) : null;
        return Ok(new PageDto<AstronautDto>(items, next));
    }

    [HttpPost("auth/sessions")]
    [EnableRateLimiting("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest body)
    {
        var user = await users.FindByIdAsync(body.AstronautId);
        var hasRole = user is not null &&
                      (await users.GetRolesAsync(user)).Any(r => r is AppRoles.Crew or AppRoles.Medic);

        if (user is null || !hasRole)
        {
            await Task.Delay(Random.Shared.Next(80, 140), HttpContext.RequestAborted);
            return Unauthorized();
        }

        var result = await signIn.CheckPasswordSignInAsync(user, body.Pin, lockoutOnFailure: true);
        if (result.IsLockedOut) return StatusCode(423);
        if (!result.Succeeded) return Unauthorized();

        var (session, response) = NewSession(user, "dev-anonymous");
        db.Sessions.Add(session);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("auth/sessions/refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest body)
    {
        if (string.IsNullOrEmpty(body.RefreshToken) || body.RefreshToken.Length is < 32 or > 512) return Unauthorized();
        var hash = TokenSecurity.Hash(body.RefreshToken);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.RefreshTokenHash == hash);
        if (session is null || session.RevokedAt is not null || session.RefreshExpiresAt <= DateTimeOffset.UtcNow)
            return Unauthorized();
        var user = await users.FindByIdAsync(session.AstronautId);
        if (user is null) return Unauthorized();
        // Device check removed: sessions created by the anonymous login use the placeholder "dev-anonymous" device.
        var (_, response) = NewSession(user, session.UnoDeviceId, session);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(response);
    }

    [HttpGet("auth/sessions/current")]
    [AllowAnonymous]
    public async Task<IActionResult> Current()
    {
        var session = await CurrentSession();
        if (session is null) return NotFound();
        var user = await users.FindByIdAsync(session.AstronautId);
        return user is null ? NotFound() : Ok(new CurrentSessionResponse(session.Id, ToAstronaut(user), session.AccessExpiresAt));
    }

    [HttpDelete("auth/sessions/current")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        var session = await CurrentSession();
        if (session is null) return NotFound();
        session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

// No bearer token means no "sid" claim, so the session ID comes from a header instead.
    private async Task<ApiSession?> CurrentSession()
    {
        var id = Request.Headers["X-Session-Id"].FirstOrDefault();
        return string.IsNullOrEmpty(id) ? null : await db.Sessions.SingleOrDefaultAsync(s => s.Id == id);
    }
    private (ApiSession Session, SessionResponse Response) NewSession(ApplicationUser user, string unoId, ApiSession? existing = null)
    {
        var now = DateTimeOffset.UtcNow;
        var access = TokenSecurity.NewToken();
        var refresh = TokenSecurity.NewToken();
        var session = existing ?? new ApiSession
        {
            Id = $"ses_{Guid.NewGuid():N}", AstronautId = user.Id, UnoDeviceId = unoId,
            AccessTokenHash = "", RefreshTokenHash = "", CreatedAt = now
        };
        session.AccessTokenHash = TokenSecurity.Hash(access);
        session.RefreshTokenHash = TokenSecurity.Hash(refresh);
        session.AccessExpiresAt = now + AccessLifetime;
        session.RefreshExpiresAt = now + RefreshLifetime;
        session.RevokedAt = null;
        var dto = ToAstronaut(user);
        return (session, new SessionResponse(access, "Bearer", (int)AccessLifetime.TotalSeconds, refresh,
            (int)RefreshLifetime.TotalSeconds, new SessionSummary(session.Id, dto)));
    }

    internal static AstronautDto ToAstronaut(ApplicationUser u) => new(u.Id, u.UserName ?? "", u.UserName ?? "");
}

public sealed record LoginRequest(string AstronautId, string Pin);
public sealed record RefreshRequest(string RefreshToken);
