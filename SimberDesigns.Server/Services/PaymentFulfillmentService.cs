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
        else if (transaction.CreditPackageId is Guid packageId)
        {
            await AcreditarCreditosAsync(transaction, packageId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsPluginPurchase(string? notes)
        => notes is not null && notes.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Suma los créditos del paquete al saldo del usuario y deja el registro. Antes esto NO se
    /// hacía: al pagar un paquete no se acreditaba nada.</summary>
    private async Task AcreditarCreditosAsync(Transaction transaction, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == transaction.UserId, cancellationToken);
        if (package is null || user is null)
        {
            return;
        }

        decimal total = package.CreditsAmount + package.BonusAmount;
        user.CreditsBalance += total;
        user.UpdatedAt = DateTime.UtcNow;
        db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TransactionId = transaction.Id,
            CreditsChanged = total,
            TxType = CreditTxTypes.Recharge,
            CreatedAt = DateTime.UtcNow
        });
    }

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
