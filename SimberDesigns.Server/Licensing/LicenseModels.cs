namespace SimberDesigns.Licensing;

/// <summary>Plan de suscripción contratado.</summary>
public enum LicensePlan
{
    Mensual,
    Anual,
    Demo
}

/// <summary>Estado resultante de verificar una licencia en la PC del cliente.</summary>
public enum LicenseStatus
{
    /// <summary>Válida y vigente.</summary>
    Valid,
    /// <summary>Firma correcta pero ya venció (hay que renovar).</summary>
    Expired,
    /// <summary>La licencia fue emitida para otra PC (otro HWID).</summary>
    WrongMachine,
    /// <summary>El texto de la licencia está alterado o la firma no coincide.</summary>
    Tampered,
    /// <summary>Formato ilegible.</summary>
    Malformed,
    /// <summary>Se detectó manipulación del reloj del sistema (rollback de fecha).</summary>
    ClockTampered
}

/// <summary>Datos que van firmados dentro de la licencia.</summary>
public sealed record LicenseInfo
{
    public required string HardwareId { get; init; }
    public required LicensePlan Plan { get; init; }
    public required DateTime IssuedUtc { get; init; }
    public required DateTime ExpiresUtc { get; init; }
    public string Customer { get; init; } = string.Empty;
    public string Edition { get; init; } = "SIMBER DESIGNS";

    /// <summary>Versión PREMIUM: incluye la lectura de listas con IA. Normal = false.</summary>
    public bool IncluyeIa { get; init; }

    /// <summary>Etiqueta comercial de versión ("PREMIUM" con IA / "STANDARD" sin IA).</summary>
    public string Version => IncluyeIa ? "PREMIUM" : "STANDARD";

    /// <summary>Cadena canónica que se firma (formato 2, con la marca de IA).</summary>
    public string ToCanonicalString()
        => string.Join("|",
            "SIMBER2",                                   // versión de formato
            HardwareId.Trim().ToUpperInvariant(),
            Plan.ToString(),
            IssuedUtc.ToUniversalTime().Ticks.ToString(),
            ExpiresUtc.ToUniversalTime().Ticks.ToString(),
            Customer.Trim(),
            Edition.Trim(),
            IncluyeIa ? "IA1" : "IA0");

    /// <summary>Cadena canónica ANTIGUA (formato 1, sin IA). Solo para verificar licencias viejas.</summary>
    internal string ToLegacyCanonicalString()
        => string.Join("|",
            "SIMBER1",
            HardwareId.Trim().ToUpperInvariant(),
            Plan.ToString(),
            IssuedUtc.ToUniversalTime().Ticks.ToString(),
            ExpiresUtc.ToUniversalTime().Ticks.ToString(),
            Customer.Trim(),
            Edition.Trim());

    public int DaysRemaining(DateTime nowUtc) => (int)Math.Ceiling((ExpiresUtc - nowUtc).TotalDays);
}

/// <summary>Resultado completo de una verificación.</summary>
public sealed record LicenseCheckResult(LicenseStatus Status, LicenseInfo? Info)
{
    public bool IsUsable => Status == LicenseStatus.Valid;

    public string SpanishMessage => Status switch
    {
        LicenseStatus.Valid => Info is null
            ? "Licencia válida."
            : $"Licencia {Info.Plan} activa. Vence el {Info.ExpiresUtc.ToLocalTime():dd/MM/yyyy}.",
        LicenseStatus.Expired => "Tu licencia venció. Envía tu código para renovar.",
        LicenseStatus.WrongMachine => "Esta licencia pertenece a otra PC.",
        LicenseStatus.Tampered => "La licencia no es válida (firma incorrecta).",
        LicenseStatus.Malformed => "El texto de la licencia está incompleto o dañado.",
        LicenseStatus.ClockTampered => "Se detectó un cambio de fecha en el sistema. Ajusta la hora real para continuar.",
        _ => "Licencia no válida."
    };
}
