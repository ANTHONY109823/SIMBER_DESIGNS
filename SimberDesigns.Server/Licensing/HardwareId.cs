using System.Security.Cryptography;
using System.Text;

namespace SimberDesigns.Licensing;

/// <summary>
/// Genera el identificador de la PC (HWID) que el cliente envía para pedir su licencia.
///
/// El HWID se deriva de señales estables de la máquina y se muestra como un código corto
/// de 5 bloques (ej: 7F3A2-9K4MB-QW8ZC-1P0RT-XY45N) fácil de copiar por WhatsApp.
///
/// La recolección de señales es reemplazable vía <see cref="Provider"/>: en producción sobre
/// Windows conviene inyectar señales fuertes (serial de disco, MachineGuid, placa madre). El
/// proveedor por defecto usa señales multiplataforma para poder probar el sistema en cualquier PC.
/// </summary>
public static class HardwareId
{
    /// <summary>Proveedor de señales de hardware. Sustituible para endurecer en Windows o para pruebas.</summary>
    public static IHardwareSignalProvider Provider { get; set; } = new DefaultHardwareSignalProvider();

    /// <summary>Devuelve el HWID normalizado de esta máquina.</summary>
    public static string Current() => Normalize(Provider.CollectRawSignature());

    /// <summary>Convierte una firma cruda de máquina en el código corto de 25 caracteres.</summary>
    public static string Normalize(string rawSignature)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawSignature));
        string b32 = Base32.Encode(hash).Substring(0, 25);
        // Agrupa en 5 bloques de 5 para lectura humana.
        var sb = new StringBuilder();
        for (int i = 0; i < b32.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(b32[i]);
        }
        return sb.ToString();
    }
}

public interface IHardwareSignalProvider
{
    /// <summary>Cadena cruda que representa de forma estable a esta máquina.</summary>
    string CollectRawSignature();
}

/// <summary>Proveedor multiplataforma por defecto (bueno para pruebas y primera versión).</summary>
public sealed class DefaultHardwareSignalProvider : IHardwareSignalProvider
{
    public string CollectRawSignature()
    {
        var parts = new[]
        {
            Environment.MachineName,
            Environment.ProcessorCount.ToString(),
            Environment.OSVersion.Platform.ToString(),
            Environment.UserDomainName,
            RuntimeMachineGuid()
        };
        return string.Join("|", parts);
    }

    // En Windows se puede leer HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid. Aquí, para
    // mantener el proyecto compilando en cualquier plataforma, se usa una señal estable de respaldo.
    private static string RuntimeMachineGuid()
        => Environment.GetEnvironmentVariable("COMPUTERNAME")
           ?? Environment.MachineName;
}

/// <summary>Proveedor de HWID fijo para pruebas deterministas.</summary>
public sealed class FixedHardwareSignalProvider(string signature) : IHardwareSignalProvider
{
    public string CollectRawSignature() => signature;
}
