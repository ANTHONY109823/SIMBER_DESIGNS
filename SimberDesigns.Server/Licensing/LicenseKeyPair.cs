using System.Security.Cryptography;

namespace SimberDesigns.Licensing;

/// <summary>
/// Par de llaves ECDSA (curva NIST P-256) del esquema de licencias.
///
/// SEGURIDAD: la llave PRIVADA se genera UNA sola vez y vive únicamente en la PC del
/// administrador (dentro de la herramienta KeyGen). Con ella se FIRMAN las licencias.
/// La llave PÚBLICA se incrusta en el plugin del cliente y solo sirve para VERIFICAR.
/// Aunque decompilen el plugin, con la pública NO se pueden fabricar licencias.
/// </summary>
public sealed class LicenseKeyPair
{
    public required string PrivateKeyBase64 { get; init; }
    public required string PublicKeyBase64 { get; init; }

    /// <summary>Crea un par de llaves nuevo. Ejecutar solo una vez para todo el producto.</summary>
    public static LicenseKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new LicenseKeyPair
        {
            PrivateKeyBase64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
            PublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())
        };
    }

    internal static ECDsa LoadPrivate(string privateKeyBase64)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return ecdsa;
    }

    internal static ECDsa LoadPublic(string publicKeyBase64)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        return ecdsa;
    }
}
