using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Services;

public interface IPaymentFulfillmentService
{
    Task FulfillAsync(Transaction transaction, string? mercadoPagoPaymentId, CancellationToken cancellationToken);
}

public sealed class PaymentFulfillmentService(
    AppDbContext db,
    IPluginPeriodKeyService periodKeys) : IPaymentFulfillmentService
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
            var edition = EditionFromNotes(transaction.Notes);
            var days = DaysFromNotes(transaction.Notes);
            // Emite el serial (Pending). NO se auto-canjea: el cliente lo pega DENTRO del .exe
            // (elige el programa en la web, que fija la edición del serial). El .exe lo canjea.
            await periodKeys.IssueAsync(
                transaction.UserId,
                edition,
                days,
                PeriodKeySources.MercadoPago,
                transaction.Id,
                $"MP {days}d",
                cancellationToken);
        }
        else if (transaction.CreditPackageId is Guid packageId)
        {
            await AcreditarCreditosAsync(transaction, packageId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsPluginPurchase(string? notes)
        => notes is not null && notes.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Notas: plugin:{días o plan}:{edition} — ej. plugin:90:Corel o plugin:month-1pc:Illustrator.</summary>
    internal static int DaysFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return 30;
        }

        var parts = notes.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return 30;
        }

        if (int.TryParse(parts[1], out var days))
        {
            return PluginPeriodKeyService.ClampDays(days);
        }

        if (string.Equals(parts[1], PluginPlans.Month1Pc, StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        return 30;
    }

    internal static string EditionFromNotes(string? notes)
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
}
