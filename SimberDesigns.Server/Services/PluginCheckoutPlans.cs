using SimberDesigns.Licensing;
using SimberDesigns.Server.Contracts;

namespace SimberDesigns.Server.Services;

/// <summary>Planes de activación: 1 / 3 / 6 / 12 meses (precio = mes × N).</summary>
public static class PluginCheckoutPlans
{
    private static readonly int[] AllowedMonths = [1, 3, 6, 12];

    public static IReadOnlyList<PluginPlanOptionDto> Build(decimal monthPricePen)
        => AllowedMonths.Select(m =>
        {
            var days = DaysForMonths(m);
            return new PluginPlanOptionDto(m, days, monthPricePen * m, LabelFor(m, days));
        }).ToList();

    public static bool TryResolve(
        string kind,
        int? monthsRequest,
        decimal monthPricePen,
        out string edition,
        out int days,
        out int months,
        out decimal amount,
        out string title)
    {
        edition = "";
        days = 30;
        months = 1;
        amount = 0;
        title = "";

        if (!TryParseKind(kind, out edition, out var kindMonths))
        {
            return false;
        }

        months = monthsRequest is int req && AllowedMonths.Contains(req) ? req : kindMonths;
        days = DaysForMonths(months);
        amount = monthPricePen * months;
        // La compra puede ser genérica (edición vacía): el programa se elige al canjear.
        var prog = string.IsNullOrWhiteSpace(edition) ? "Simber Designs" : LicenseProgram.Etiqueta(edition);
        title = months == 1
            ? $"Activación {days} días · {prog} · US$15"
            : $"Activación {months} meses ({days} días) · {prog} · US${15 * months}";
        return true;
    }

    private static bool TryParseKind(string kind, out string edition, out int months)
    {
        edition = "";
        months = 1;
        kind = (kind ?? "").Trim().ToLowerInvariant();

        // Plan genérico (sin programa): el cliente elige Corel/Illustrator al canjear.
        if (kind is "plugin" or "plugin-any" or "plugin-1m") { edition = ""; months = 1; return true; }
        if (kind is "plugin-3m" or "plugin-any-3m") { edition = ""; months = 3; return true; }
        if (kind is "plugin-6m" or "plugin-any-6m") { edition = ""; months = 6; return true; }
        if (kind is "plugin-12m" or "plugin-any-12m") { edition = ""; months = 12; return true; }

        if (kind is "plugin-corel" or "corel" or "plugin-corel-1m")
        {
            edition = LicenseProgram.Corel;
            months = 1;
            return true;
        }

        if (kind is "plugin-corel-3m")
        {
            edition = LicenseProgram.Corel;
            months = 3;
            return true;
        }

        if (kind is "plugin-corel-6m")
        {
            edition = LicenseProgram.Corel;
            months = 6;
            return true;
        }

        if (kind is "plugin-corel-12m")
        {
            edition = LicenseProgram.Corel;
            months = 12;
            return true;
        }

        if (kind is "plugin-illustrator" or "plugin-ilus" or "illustrator" or "ilus" or "plugin-illustrator-1m" or "plugin-ilus-1m")
        {
            edition = LicenseProgram.Illustrator;
            months = 1;
            return true;
        }

        if (kind is "plugin-illustrator-3m" or "plugin-ilus-3m")
        {
            edition = LicenseProgram.Illustrator;
            months = 3;
            return true;
        }

        if (kind is "plugin-illustrator-6m" or "plugin-ilus-6m")
        {
            edition = LicenseProgram.Illustrator;
            months = 6;
            return true;
        }

        if (kind is "plugin-illustrator-12m" or "plugin-ilus-12m")
        {
            edition = LicenseProgram.Illustrator;
            months = 12;
            return true;
        }

        return false;
    }

    private static int DaysForMonths(int months) => months switch
    {
        3 => 90,
        6 => 180,
        12 => 365,
        _ => 30
    };

    private static string LabelFor(int months, int days) => months switch
    {
        1 => $"1 mes ({days} días)",
        3 => $"3 meses ({days} días)",
        6 => $"6 meses ({days} días)",
        12 => $"12 meses ({days} días)",
        _ => $"{months} meses"
    };
}
