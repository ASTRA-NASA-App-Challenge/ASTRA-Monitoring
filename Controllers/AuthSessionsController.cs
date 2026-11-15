using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringApi.Data;

namespace MonitoringApi.Controllers;

[ApiController]
[Route("api/v1/crew")]
public sealed class CrewController(ApiDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register(RegisterCrewRequest body)
    {
        if (!CrewMember.IsValid(body.Pin)) return BadRequest("PIN must contain exactly six ASCII digits.");
        // Atomic insertion handles concurrent registration without duplicate records.
        var added = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CrewMembers (Pin) VALUES ({body.Pin}) ON CONFLICT(Pin) DO NOTHING", HttpContext.RequestAborted);
        return added == 0 ? Conflict("PIN already registered.") : Created($"/api/v1/crew/{body.Pin}", new { body.Pin });
    }

    [HttpGet("{pin}")]
    public async Task<IActionResult> Get(string pin) =>
        CrewMember.IsValid(pin) && await db.CrewMembers.AnyAsync(x => x.Pin == pin)
            ? Ok(new { pin }) : NotFound();

    [HttpGet("/api/v1/healthz")]
    public IActionResult Health() => Ok(new { status = "ok" });
}
public sealed record RegisterCrewRequest([Required, RegularExpression("^[0-9]{6}$")] string Pin);

