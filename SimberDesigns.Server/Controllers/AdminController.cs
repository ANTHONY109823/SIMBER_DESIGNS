using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

/// <summary>Datos del panel de administración: métricas del negocio e historial de licencias vendidas.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin")]
public sealed class AdminController(
    AppDbContext db,
    IPluginPeriodKeyService periodKeys,
    PasswordHasher<User> passwordHasher) : ControllerBase
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

    /// <summary>Crea cuenta (sorteo / Yape). Opcionalmente emite clave de periodo y fuerza cambio de contraseña.</summary>
    [HttpPost("customers")]
    public async Task<ActionResult<AdminCreateCustomerResponse>> CreateCustomer(
        AdminCreateCustomerRequest request,
        CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return BadRequest("Correo inválido.");
        }

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Conflict("Ya existe una cuenta con ese correo.");
        }

        var temp = string.IsNullOrWhiteSpace(request.TemporaryPassword)
            ? $"Simber{Random.Shared.Next(100000, 999999)}"
            : request.TemporaryPassword.Trim();
        if (temp.Length < 6)
        {
            return BadRequest("La contraseña temporal debe tener al menos 6 caracteres.");
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? email : request.FullName.Trim(),
            Role = Roles.Customer,
            CreditsBalance = 0,
            MustChangePassword = request.MustChangePassword,
            CreatedAt = now,
            UpdatedAt = now
        };
        user.PasswordHash = passwordHasher.HashPassword(user, temp);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        PluginPeriodKeyDto? keyDto = null;
        if (!string.IsNullOrWhiteSpace(request.Edition) && request.Days is > 0)
        {
            try
            {
                var key = await periodKeys.IssueAsync(
                    user.Id,
                    request.Edition,
                    request.Days.Value,
                    PeriodKeySources.Manual,
                    null,
                    request.Note,
                    ct);
                keyDto = ToKeyDto(key);
            }
            catch (Exception ex)
            {
                return BadRequest($"Usuario creado ({user.Email}), pero no se emitió la clave: {ex.Message}");
            }
        }

        return Ok(new AdminCreateCustomerResponse(user.Id, user.Email, temp, keyDto));
    }

    /// <summary>Emite serial de periodo (pago directo / sorteo) para un usuario existente.</summary>
    [HttpPost("licenses/issue")]
    public async Task<ActionResult<PluginPeriodKeyDto>> IssuePeriodKey(
        AdminIssuePeriodKeyRequest request,
        CancellationToken ct)
    {
        User? user = null;
        if (request.UserId is Guid uid)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        }
        else if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var email = request.Email.Trim().ToLowerInvariant();
            user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        }

        if (user is null)
        {
            return NotFound("No encontramos ese cliente.");
        }

        if (user.Role == Roles.Admin)
        {
            return BadRequest("No se emiten claves a un administrador.");
        }

        try
        {
            var key = await periodKeys.IssueAsync(
                user.Id,
                request.Edition,
                request.Days <= 0 ? 30 : request.Days,
                PeriodKeySources.Manual,
                null,
                request.Note,
                ct);
            return Ok(ToKeyDto(key));
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("period-keys")]
    public async Task<ActionResult<IReadOnlyList<AdminPeriodKeyDto>>> PeriodKeys([FromQuery] string? q, CancellationToken ct)
    {
        var query = db.PluginPeriodKeys.Include(k => k.User).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(k =>
                k.User.Email.ToLower().Contains(term)
                || k.Code.ToLower().Contains(term)
                || (k.Note != null && k.Note.ToLower().Contains(term)));
        }

        var list = await query
            .OrderByDescending(k => k.CreatedAt)
            .Take(500)
            .Select(k => new AdminPeriodKeyDto(
                k.Id,
                k.User.Email,
                k.User.FullName,
                k.Code,
                k.Edition,
                k.Days,
                k.Status,
                k.Source,
                k.Note,
                k.CreatedAt,
                k.RedeemedAt))
            .ToListAsync(ct);
        return Ok(list);
    }

    private static PluginPeriodKeyDto ToKeyDto(PluginPeriodKey key)
        => new(key.Id, key.Code, key.Edition, key.Days, key.Status, key.Source, key.Note, key.CreatedAt, key.RedeemedAt);
}
