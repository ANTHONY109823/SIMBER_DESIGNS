using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountController(AppDbContext db) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<AccountDashboardDto>> Dashboard(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var subscription = await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == user.Id && s.ExpiresAt > now && s.Status != SubscriptionStatuses.Expired)
            .OrderByDescending(s => s.DailyDownloadLimit)
            .FirstOrDefaultAsync(cancellationToken);

        var downloadsToday = await db.UserDownloads.CountAsync(
            d => d.UserId == user.Id && d.DownloadedAt >= now.AddDays(-1),
            cancellationToken);

        var purchasedIds = await db.CreditTransactions
            .AsNoTracking()
            .Where(c => c.UserId == user.Id && c.TxType == CreditTxTypes.PurchaseDesign && c.DesignId != null)
            .Select(c => c.DesignId!.Value)
            .ToListAsync(cancellationToken);

        var boughtIds = await db.Transactions
            .AsNoTracking()
            .Where(t => t.UserId == user.Id && t.Status == TransactionStatuses.Completed && t.DesignId != null)
            .Select(t => t.DesignId!.Value)
            .ToListAsync(cancellationToken);

        var unlockedIds = purchasedIds.Concat(boughtIds).Distinct().ToList();
        var unlockedDesigns = await db.Designs
            .AsNoTracking()
            .Where(d => unlockedIds.Contains(d.Id))
            .Select(d => new UnlockedDesignDto(d.Id, d.Title, d.Slug, d.PreviewUrl, false))
            .ToListAsync(cancellationToken);

        if (subscription is not null && subscription.Status == SubscriptionStatuses.Active)
        {
            var extra = await db.UserDownloads
                .AsNoTracking()
                .Where(d => d.UserId == user.Id)
                .Select(d => d.Design)
                .Distinct()
                .Select(d => new UnlockedDesignDto(d!.Id, d.Title, d.Slug, d.PreviewUrl, true))
                .ToListAsync(cancellationToken);

            foreach (var item in extra.Where(item => unlockedDesigns.All(u => u.Id != item.Id)))
            {
                unlockedDesigns.Add(item);
            }
        }

        var transactions = await db.Transactions
            .AsNoTracking()
            .Include(t => t.CreditPackage)
            .Include(t => t.Subscription)
            .Include(t => t.Design)
            .Where(t => t.UserId == user.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var txDtos = transactions.Select(t => new TransactionDto(
            t.Id,
            t.CreditPackage?.Name is string pack ? $"Paquete {pack}"
                : t.Subscription?.Tier is string tier ? $"Membresía {tier}"
                : t.Design?.Title ?? t.Notes ?? "Transacción",
            t.Amount,
            t.Currency,
            t.Gateway,
            t.Status,
            t.ExternalReferenceId,
            t.PaymentReceiptUrl,
            t.Notes,
            t.CreatedAt)).ToList();

        var creditLogs = await db.CreditTransactions
            .AsNoTracking()
            .Include(c => c.Design)
            .Where(c => c.UserId == user.Id)
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .Select(c => new CreditLogDto(
                c.Id,
                c.TxType == CreditTxTypes.PurchaseDesign && c.Design != null
                    ? $"Canje de diseño: {c.Design.Title}"
                    : c.TxType == CreditTxTypes.Recharge ? "Recarga de créditos"
                    : c.TxType,
                c.CreditsChanged,
                c.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(new AccountDashboardDto(
            user.Email,
            user.FullName,
            user.Role,
            user.CreditsBalance,
            subscription?.Tier,
            subscription?.Status,
            subscription?.ExpiresAt,
            subscription?.DailyDownloadLimit ?? 0,
            downloadsToday,
            unlockedDesigns,
            txDtos,
            creditLogs));
    }
}
