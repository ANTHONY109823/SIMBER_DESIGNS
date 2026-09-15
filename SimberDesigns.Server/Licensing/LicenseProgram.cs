namespace SimberDesigns.Licensing;

/// <summary>
/// Etiqueta de programa que viaja en <see cref="LicenseInfo.Edition"/>.
/// Copia del firmador del escritorio: no cambiar el formato del token.
/// </summary>
public static class LicenseProgram
{
    public const string Corel = "Corel";
    public const string Illustrator = "Illustrator";
    public const string Legacy = "SIMBER DESIGNS";

    public static bool CompatibleCon(string? edition, string esperado)
    {
        string ed = (edition ?? "").Trim();
        if (ed.Length == 0 || ed.Equals(Legacy, StringComparison.OrdinalIgnoreCase))
            return true;
        return ed.Contains(esperado, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalizar(string? edition)
    {
        string ed = (edition ?? "").Trim();
        if (ed.Length == 0 || ed.Equals(Legacy, StringComparison.OrdinalIgnoreCase))
            return "";
        if (ed.Contains("Illustrator", StringComparison.OrdinalIgnoreCase) ||
            ed.Contains("Ilustr", StringComparison.OrdinalIgnoreCase))
            return Illustrator;
        if (ed.Contains("Corel", StringComparison.OrdinalIgnoreCase))
            return Corel;
        return ed;
    }

    public static string Etiqueta(string? programa)
    {
        string n = Normalizar(programa);
        if (n == Corel) return "CorelDRAW";
        if (n == Illustrator) return "Illustrator";
        return "sin programa";
    }
}
