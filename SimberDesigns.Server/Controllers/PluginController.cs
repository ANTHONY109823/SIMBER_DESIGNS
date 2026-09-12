using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/plugin")]
public sealed class PluginController(AppDbContext db) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<PluginLicenseDto>> Mine(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var license = await db.PluginLicenses
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (license is null)
        {
            return NotFound("Aún no hay licencia del plugin. Paga el mes en Recargar.");
        }

        var active = license.Status == PluginLicenseStatuses.Active && license.ExpiresAt > DateTime.UtcNow;
        return Ok(new PluginLicenseDto(
            license.Plan,
            active ? PluginLicenseStatuses.Active : PluginLicenseStatuses.Expired,
            license.ActivationCode,
            license.ExpiresAt,
            active,
            license.HardwareId));
    }
}
