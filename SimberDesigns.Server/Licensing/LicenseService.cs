using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimberDesigns.Licensing;

/// <summary>
/// FIRMADOR — lado administrador. Vive dentro de la herramienta KeyGen con la llave privada.
/// Toma el HWID del cliente + el plan + la fecha de vencimiento y produce el texto de licencia.
/// </summary>
public sealed class LicenseSigner
{
    private readonly string _privateKeyBase64;

    public LicenseSigner(string privateKeyBase64) => _privateKeyBase64 = privateKeyBase64;

    /// <summary>Emite una licencia nueva para un cliente. <paramref name="incluyeIa"/> = versión Premium.</summary>
    public string Issue(string hardwareId, LicensePlan plan, DateTime expiresUtc, string customer = "", bool incluyeIa = false)
    {
        var info = new LicenseInfo
        {
            HardwareId = hardwareId,
            Plan = plan,
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = expiresUtc.ToUniversalTime(),
            Customer = customer,
            IncluyeIa = incluyeIa
        };
        return Sign(info);
    }

    /// <summary>
    /// Renovación: reutiliza el HWID y el cliente de una licencia previa y solo extiende la fecha.
    /// Refleja el flujo "el cliente reenvía su llave, damos renovar y le mandamos la nueva".
    /// </summary>
    public string Renew(LicenseInfo previous, DateTime newExpiresUtc)
    {
        var info = previous with
        {
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = newExpiresUtc.ToUniversalTime()
        };
        return Sign(info);
    }

    public string Sign(LicenseInfo info)
    {
        using var ecdsa = LicenseKeyPair.LoadPrivate(_privateKeyBase64);
        byte[] signature = ecdsa.SignData(
            Encoding.UTF8.GetBytes(info.ToCanonicalString()), HashAlgorithmName.SHA256);
        return LicenseToken.Encode(info, signature);
    }
}

/// <summary>
/// VERIFICADOR — lado cliente. Vive dentro del plugin con SOLO la llave pública.
/// Comprueba firma, máquina, reloj y vencimiento.
/// </summary>
public sealed class LicenseVerifier
{
    private readonly string _publicKeyBase64;
    private readonly IClockGuard _clockGuard;

    public LicenseVerifier(string publicKeyBase64, IClockGuard? clockGuard = null)
    {
        _publicKeyBase64 = publicKeyBase64;
        _clockGuard = clockGuard ?? new NullClockGuard();
    }

    public LicenseCheckResult Verify(string licenseText, string currentHardwareId, DateTime? nowUtc = null)
    {
        DateTime now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();

        if (!LicenseToken.TryDecode(licenseText, out var info, out var signature) || info is null)
            return new LicenseCheckResult(LicenseStatus.Malformed, null);

        // 1) Autenticidad: la firma debe validar con la llave pública.
        //    Se acepta el formato nuevo (con IA) y, por compatibilidad, el antiguo (sin IA).
        using (var ecdsa = LicenseKeyPair.LoadPublic(_publicKeyBase64))
        {
            bool ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(info.ToCanonicalString()), signature, HashAlgorithmName.SHA256)
                   || ecdsa.VerifyData(Encoding.UTF8.GetBytes(info.ToLegacyCanonicalString()), signature, HashAlgorithmName.SHA256);
            if (!ok) return new LicenseCheckResult(LicenseStatus.Tampered, info);
        }

        // 2) Máquina correcta.
        if (!string.Equals(info.HardwareId.Trim(), currentHardwareId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return new LicenseCheckResult(LicenseStatus.WrongMachine, info);

        // 3) Anti-rollback: nadie debe poder atrasar el reloj para revivir una licencia.
        if (_clockGuard.IsRolledBack(now))
            return new LicenseCheckResult(LicenseStatus.ClockTampered, info);
        _clockGuard.Remember(now);

        // 4) Vigencia.
        if (now > info.ExpiresUtc)
            return new LicenseCheckResult(LicenseStatus.Expired, info);

        return new LicenseCheckResult(LicenseStatus.Valid, info);
    }
}

/// <summary>
/// Lectura pública de una licencia SIN verificar firma. La usa el KeyGen para renovar
/// (leer HWID/cliente de una licencia previa) y la UI para mostrar datos. NUNCA la uses
/// para decidir acceso: para eso está <see cref="LicenseVerifier"/>, que sí valida la firma.
/// </summary>
public static class LicenseReader
{
    public static bool TryRead(string licenseText, out LicenseInfo? info)
        => LicenseToken.TryDecode(licenseText, out info, out _);
}

/// <summary>Codifica/decodifica el texto de licencia: campos + firma en un token copiable.</summary>
internal static class LicenseToken
{
    private sealed record Wire(string hwid, string plan, long iss, long exp, string cust, string ed, bool ia = false);

    public static string Encode(LicenseInfo info, byte[] signature)
    {
        var wire = new Wire(info.HardwareId, info.Plan.ToString(),
            info.IssuedUtc.ToUniversalTime().Ticks, info.ExpiresUtc.ToUniversalTime().Ticks,
            info.Customer, info.Edition, info.IncluyeIa);
        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(wire));
        string sig = Base64Url(signature);
        return $"SIMBER.{payload}.{sig}";
    }

    public static bool TryDecode(string text, out LicenseInfo? info, out byte[] signature)
    {
        info = null;
        signature = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        string cleaned = text.Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "");
        var parts = cleaned.Split('.');
        if (parts.Length != 3 || parts[0] != "SIMBER") return false;

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(FromBase64Url(parts[1]));
            if (wire is null) return false;
            if (!Enum.TryParse<LicensePlan>(wire.plan, out var plan)) return false;

            info = new LicenseInfo
            {
                HardwareId = wire.hwid,
                Plan = plan,
                IssuedUtc = new DateTime(wire.iss, DateTimeKind.Utc),
                ExpiresUtc = new DateTime(wire.exp, DateTimeKind.Utc),
                Customer = wire.cust,
                Edition = wire.ed,
                IncluyeIa = wire.ia
            };
            signature = FromBase64Url(parts[2]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
        return Convert.FromBase64String(b64);
    }
}
