using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;
using MonitoringApi.Services;
using Microsoft.AspNetCore.Authorization;

namespace MonitoringApi.Controllers;

[ApiController]
[Route("api/v1/crew")]
[Authorize(Policy = "web")]
public sealed class CrewController(ApiDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register(RegisterCrewRequest body)
    {
        if (!CrewMember.IsValid(body.Pin)) return BadRequest("PIN must contain exactly six ASCII digits.");
        // Atomic insertion handles concurrent registration without duplicate records.
        await using var tx = await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        var added = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CrewMembers (Pin, Retired, DeletionPending) VALUES ({body.Pin}, 0, 0) ON CONFLICT(Pin) DO NOTHING", HttpContext.RequestAborted);
        var retired = await db.CrewMembers.AnyAsync(x => x.Pin == body.Pin && x.Retired);
        await tx.CommitAsync(HttpContext.RequestAborted);
        if (retired) return StatusCode(410);
        return added == 0 ? Ok(new { body.Pin }) : Created($"/api/v1/crew/{body.Pin}", new { body.Pin });
    }

    [HttpGet("{pin}")]
    public async Task<IActionResult> Get(string pin) =>
        CrewMember.IsValid(pin) && await db.CrewMembers.AnyAsync(x => x.Pin == pin && !x.Retired)
            ? Ok(new { pin }) : NotFound();

    [HttpGet("/api/v1/healthz")]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new { status = "ok" });

    [HttpDelete("{pin}")]
    public async Task<IActionResult> Delete(string pin, [FromServices] CrewDeletion deletion)
    {
        if (!CrewMember.IsValid(pin)) return BadRequest();
        await deletion.RetireAsync(pin, HttpContext.RequestAborted);
        try { await deletion.PurgePhotosAsync(pin, HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        { return Accepted(new { status = "photo_cleanup_pending" }); }
        return NoContent();
    }
}
public sealed record RegisterCrewRequest([Required, RegularExpression("^[0-9]{6}$")] string Pin);
