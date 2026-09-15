using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;
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
            await ExtendPluginLicenseAsync(transaction.UserId, EditionFromNotes(transaction.Notes), cancellationToken);
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

    private async Task ExtendPluginLicenseAsync(Guid userId, string edition, CancellationToken cancellationToken)
    {
        var licenses = await db.PluginLicenses
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .ToListAsync(cancellationToken);

        PluginLicense? license = null;
        if (!string.IsNullOrWhiteSpace(edition))
        {
            license = licenses.FirstOrDefault(l => string.Equals(l.Edition, edition, StringComparison.OrdinalIgnoreCase))
                      ?? licenses.FirstOrDefault(l => string.IsNullOrWhiteSpace(l.Edition));
        }
        else
        {
            license = licenses.FirstOrDefault();
        }

        var now = DateTime.UtcNow;
        if (license is null)
        {
            db.PluginLicenses.Add(new PluginLicense
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = PluginPlans.Month1Pc,
                Status = PluginLicenseStatuses.Active,
                Edition = edition,
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
        if (string.IsNullOrWhiteSpace(license.Edition) && !string.IsNullOrWhiteSpace(edition))
        {
            license.Edition = edition;
        }

        license.ActivationCode = NewActivationCode();
    }

    private static string EditionFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "";
        }

        var parts = notes.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
        {
            return LicenseProgram.Normalizar(parts[2]);
        }

        return "";
    }

    private static string NewActivationCode()
        => $"SIM-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
}
