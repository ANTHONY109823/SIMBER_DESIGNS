using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Controllers;

/// <summary>Datos del panel de administración: métricas del negocio e historial de licencias vendidas.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin")]
public sealed class AdminController(AppDbContext db) : ControllerBase
{
    [HttpGet("metrics")]
    public async Task<ActionResult<AdminMetricsDto>> Metrics(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        int totalUsers = await db.Users.CountAsync(ct);
        int activeLicenses = await db.PluginLicenses.CountAsync(l => l.Status == PluginLicenseStatuses.Active && l.ExpiresAt > now, ct);
        int expiredLicenses = await db.PluginLicenses.CountAsync(l => l.ExpiresAt <= now, ct);

        var completed = db.Transactions.Where(t => t.Status == TransactionStatuses.Completed);
        int salesThisMonth = await completed.CountAsync(t => t.CreatedAt >= monthStart, ct);
        decimal revenueThisMonth = await completed.Where(t => t.CreatedAt >= monthStart).SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
        decimal totalRevenue = await completed.SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
        int pending = await db.Transactions.CountAsync(t => t.Status == TransactionStatuses.Pending, ct);

        return Ok(new AdminMetricsDto(totalUsers, activeLicenses, expiredLicenses, salesThisMonth, revenueThisMonth, totalRevenue, pending));
    }

    /// <summary>Historial de licencias del plugin: a quién se vendió, plan, estado, PC y vencimiento.
    /// Vive aquí para corregir/consultar si un cliente reclama. Búsqueda opcional por email.</summary>
    [HttpGet("licenses")]
    public async Task<ActionResult<IReadOnlyList<AdminLicenseDto>>> Licenses([FromQuery] string? q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.PluginLicenses.Include(l => l.User).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            string term = q.Trim().ToLower();
            query = query.Where(l => l.User.Email.ToLower().Contains(term) || (l.HardwareId != null && l.HardwareId.Contains(term)));
        }

        var list = await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(500)
            .Select(l => new AdminLicenseDto(
                l.User.Email,
                l.User.FullName,
                l.Plan,
                l.ExpiresAt > now ? PluginLicenseStatuses.Active : PluginLicenseStatuses.Expired,
                l.ExpiresAt > now,
                l.ExpiresAt,
                l.HardwareId,
                l.ActivationCode,
                l.CreatedAt))
            .ToListAsync(ct);

        return Ok(list);
    }
}
