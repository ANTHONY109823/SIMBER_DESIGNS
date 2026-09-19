using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Services;

public interface IPluginPeriodKeyService
{
    Task<PluginPeriodKey> IssueAsync(
        Guid userId,
        string edition,
        int days,
        string source,
        Guid? transactionId,
        string? note,
        CancellationToken cancellationToken);

    /// <summary>
    /// Canjea un serial pendiente: suma días a la licencia del programa (sin atar HWID).
    /// Si el serial es genérico (sin edición), <paramref name="chosenEdition"/> define el programa
    /// (Corel o Illustrator) que el cliente eligió al canjear.
    /// </summary>
    Task<(PluginLicense License, PluginPeriodKey Key)> RedeemAsync(
        Guid userId,
        string code,
        string? chosenEdition,
        CancellationToken cancellationToken);
}

public sealed class PluginPeriodKeyService(AppDbContext db) : IPluginPeriodKeyService
{
    public const int MinDays = 1;
    public const int MaxDays = 730;

    public async Task<PluginPeriodKey> IssueAsync(
        Guid userId,
        string edition,
        int days,
        string source,
        Guid? transactionId,
        string? note,
        CancellationToken cancellationToken)
    {
        days = ClampDays(days);
        // La edición puede quedar vacía a propósito: la compra es genérica y el cliente elige
        // Corel o Illustrator al canjear el serial (RedeemAsync).
        edition = LicenseProgram.Normalizar(edition);

        var now = DateTime.UtcNow;
        var key = new PluginPeriodKey
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Code = await NewUniqueCodeAsync(edition, cancellationToken),
            Edition = edition,
            Days = days,
            Status = PeriodKeyStatuses.Pending,
            Source = string.IsNullOrWhiteSpace(source) ? PeriodKeySources.Manual : source.Trim(),
            TransactionId = transactionId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 300)],
            CreatedAt = now
        };
        db.PluginPeriodKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public async Task<(PluginLicense License, PluginPeriodKey Key)> RedeemAsync(
        Guid userId,
        string code,
        string? chosenEdition,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length < 8)
        {
            throw new InvalidOperationException("Clave inválida.");
        }

        var key = await db.PluginPeriodKeys
            .FirstOrDefaultAsync(k => k.Code == normalized, cancellationToken);
        if (key is null)
        {
            throw new InvalidOperationException("No existe esa clave.");
        }

        if (key.UserId != userId)
        {
            throw new InvalidOperationException("Esa clave no pertenece a tu cuenta.");
        }

        if (key.Status == PeriodKeyStatuses.Revoked)
        {
            throw new InvalidOperationException("Esa clave fue anulada.");
        }

        if (key.Status == PeriodKeyStatuses.Redeemed)
        {
            throw new InvalidOperationException("Esa clave ya fue canjeada.");
        }

        // Si el serial ya está amarrado a un programa, solo se puede activar en ESE programa.
        // (chosenEdition = el programa del .exe que intenta canjear.)
        var chosen = LicenseProgram.Normalizar(chosenEdition);
        if (!string.IsNullOrWhiteSpace(key.Edition)
            && !string.IsNullOrWhiteSpace(chosen)
            && !string.Equals(key.Edition, chosen, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Este serial es para {LicenseProgram.Etiqueta(key.Edition)}. Ábrelo en ese programa.");
        }

        // Edición efectiva: la del serial si ya la trae; si es genérico, la que el cliente eligió al canjear.
        var effectiveEdition = !string.IsNullOrWhiteSpace(key.Edition)
            ? key.Edition
            : chosen;

        var license = await ExtendOrCreateLicenseAsync(userId, effectiveEdition, key.Days, cancellationToken);
        key.Status = PeriodKeyStatuses.Redeemed;
        key.RedeemedAt = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(key.Edition) && !string.IsNullOrWhiteSpace(effectiveEdition))
        {
            key.Edition = effectiveEdition; // deja registrado el programa elegido
        }
        await db.SaveChangesAsync(cancellationToken);
        return (license, key);
    }

    public static int ClampDays(int days) => Math.Clamp(days, MinDays, MaxDays);

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "";
        }

        return string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    public static bool LooksLikePeriodKey(string? code)
    {
        var n = NormalizeCode(code);
        return n.StartsWith("SMK-", StringComparison.Ordinal);
    }

    private async Task<PluginLicense> ExtendOrCreateLicenseAsync(
        Guid userId,
        string edition,
        int days,
        CancellationToken cancellationToken)
    {
        days = ClampDays(days);
        edition = LicenseProgram.Normalizar(edition);
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
            license = new PluginLicense
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = PluginPlans.Month1Pc,
                Status = PluginLicenseStatuses.Active,
                Edition = edition,
                ActivationCode = NewLegacyActivationCode(),
                ExpiresAt = now.AddDays(days),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.PluginLicenses.Add(license);
            return license;
        }

        var start = license.ExpiresAt > now ? license.ExpiresAt : now;
        license.ExpiresAt = start.AddDays(days);
        license.Status = PluginLicenseStatuses.Active;
        license.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(license.Edition) && !string.IsNullOrWhiteSpace(edition))
        {
            license.Edition = edition;
        }

        return license;
    }

    private async Task<string> NewUniqueCodeAsync(string edition, CancellationToken cancellationToken)
    {
        // Serial de 16 caracteres alfanuméricos (sin prefijo; la EDICIÓN se guarda en la fila del key,
        // no en el serial). Alfabeto sin caracteres confusos. `edition` se conserva por compatibilidad.
        _ = edition;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var code = RandomSuffix(16);
            if (!await db.PluginPeriodKeys.AnyAsync(k => k.Code == code, cancellationToken))
            {
                return code;
            }
        }

        return Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
    }

    private static string RandomSuffix(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    private static string NewLegacyActivationCode()
        => $"SIM-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
}
