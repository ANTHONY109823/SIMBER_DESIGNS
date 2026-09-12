using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Services;

public interface IPaymentFulfillmentService
{
    Task FulfillAsync(Transaction transaction, string? mercadoPagoPaymentId, CancellationToken cancellationToken);
}

public sealed class PaymentFulfillmentService(AppDbContext db) : IPaymentFulfillmentService
{
    public async Task FulfillAsync(Transaction transaction, string? mercadoPagoPaymentId, CancellationToken cancellationToken)
    {
        if (transaction.Status == TransactionStatuses.Completed)
        {
            return;
        }

        transaction.Status = TransactionStatuses.Completed;
        transaction.VerifiedAt = DateTime.UtcNow;
        transaction.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(mercadoPagoPaymentId))
        {
            transaction.ExternalReferenceId = mercadoPagoPaymentId;
        }

        if (IsPluginPurchase(transaction.Notes))
        {
            await ExtendPluginLicenseAsync(transaction.UserId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsPluginPurchase(string? notes)
        => notes is not null && notes.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase);

    private async Task ExtendPluginLicenseAsync(Guid userId, CancellationToken cancellationToken)
    {
        var license = await db.PluginLicenses
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc, cancellationToken);
        var now = DateTime.UtcNow;
        if (license is null)
        {
            db.PluginLicenses.Add(new PluginLicense
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = PluginPlans.Month1Pc,
                Status = PluginLicenseStatuses.Active,
                ActivationCode = NewActivationCode(),
                ExpiresAt = now.AddDays(30),
                CreatedAt = now,
                UpdatedAt = now
            });
            return;
        }

        var start = license.ExpiresAt > now ? license.ExpiresAt : now;
        license.ExpiresAt = start.AddDays(30);
        license.Status = PluginLicenseStatuses.Active;
        license.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(license.ActivationCode))
        {
            license.ActivationCode = NewActivationCode();
        }
    }

    private static string NewActivationCode()
        => $"SIM-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
}
