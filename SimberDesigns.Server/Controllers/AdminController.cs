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
        // Solo pagos hechos (Completed). Los Pending/Rejected de intentos fallidos no cuentan.
        int paidTotal = await completed.CountAsync(ct);

        // Cierra intentos de checkout abandonados para que no ensucien el panel.
        var staleCutoff = now.AddHours(-1);
        var abandoned = await db.Transactions
            .Where(t => t.Status == TransactionStatuses.Pending && t.CreatedAt < staleCutoff)
            .ToListAsync(ct);
        if (abandoned.Count > 0)
        {
            foreach (var tx in abandoned)
            {
                tx.Status = TransactionStatuses.Failed;
                tx.UpdatedAt = now;
                tx.Notes = string.IsNullOrWhiteSpace(tx.Notes)
                    ? "abandoned_checkout"
                    : $"{tx.Notes}|abandoned_checkout";
            }

            await db.SaveChangesAsync(ct);
        }

        return Ok(new AdminMetricsDto(totalUsers, activeLicenses, expiredLicenses, salesThisMonth, revenueThisMonth, totalRevenue, paidTotal));
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
                l.CreatedAt,
                l.Edition))
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("customers")]
    public async Task<ActionResult<IReadOnlyList<AdminCustomerDto>>> Customers([FromQuery] string? q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.FullName.ToLower().Contains(term));
        }

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Take(500)
            .ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var activeByUser = await db.PluginLicenses.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) && l.Status == PluginLicenseStatuses.Active && l.ExpiresAt > now)
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        return Ok(users.Select(u => new AdminCustomerDto(
            u.Id,
            u.Email,
            u.FullName,
            u.Role,
            u.CreditsBalance,
            activeByUser.GetValueOrDefault(u.Id),
            u.CreatedAt)).ToList());
    }

    [HttpPost("customers/{id:guid}/credits")]
    public async Task<ActionResult<AdminCustomerDto>> AdjustCredits(Guid id, AdjustCreditsRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Role == Roles.Admin)
        {
            return BadRequest("No se ajustan créditos de un administrador.");
        }

        var delta = request.Credits;
        user.CreditsBalance = Math.Max(0, user.CreditsBalance + delta);
        user.UpdatedAt = DateTime.UtcNow;
        db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreditsChanged = delta,
            TxType = delta >= 0 ? CreditTxTypes.Recharge : CreditTxTypes.Refund,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var active = await db.PluginLicenses.CountAsync(
            l => l.UserId == user.Id && l.Status == PluginLicenseStatuses.Active && l.ExpiresAt > now, ct);

        return Ok(new AdminCustomerDto(user.Id, user.Email, user.FullName, user.Role, user.CreditsBalance, active, user.CreatedAt));
    }
}
