using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Services;

public static class MembershipLimits
{
    public static int ForTier(string? tier) => tier switch
    {
        MembershipTiers.Basic => 15,
        MembershipTiers.Vip => 25,
        MembershipTiers.Semestral => 30,
        _ => 0
    };

    public static bool CanDownload(int usedInWindow, int dailyLimit) => usedInWindow < dailyLimit;

    public static int Remaining(int usedInWindow, int dailyLimit) => Math.Max(0, dailyLimit - usedInWindow);
}

public interface IDownloadLimitService
{
    Task<(Subscription? Subscription, int Used, int Limit, int Remaining, bool Allowed)> EvaluateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class DownloadLimitService(AppDbContext db) : IDownloadLimitService
{
    public async Task<(Subscription? Subscription, int Used, int Limit, int Remaining, bool Allowed)> EvaluateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var subscription = await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.Status == SubscriptionStatuses.Active
                        && s.ExpiresAt > now)
            .OrderByDescending(s => s.DailyDownloadLimit)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
        {
            return (null, 0, 0, 0, false);
        }

        var windowStart = now.AddDays(-1);
        var used = await db.UserDownloads.CountAsync(
            x => x.UserId == userId && x.DownloadedAt >= windowStart,
            cancellationToken);

        var remaining = MembershipLimits.Remaining(used, subscription.DailyDownloadLimit);
        return (subscription, used, subscription.DailyDownloadLimit, remaining,
            MembershipLimits.CanDownload(used, subscription.DailyDownloadLimit));
    }
}
