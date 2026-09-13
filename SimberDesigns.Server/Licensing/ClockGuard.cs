using System.Security.Cryptography;
using System.Text;

namespace SimberDesigns.Licensing;

/// <summary>Defensa contra el atraso del reloj para revivir una licencia vencida.</summary>
public interface IClockGuard
{
    /// <summary>¿La fecha actual es anterior a la última fecha vista (más allá de una tolerancia)?</summary>
    bool IsRolledBack(DateTime nowUtc);

    /// <summary>Registra la fecha actual como la más reciente conocida.</summary>
    void Remember(DateTime nowUtc);
}

/// <summary>Guarda sin efecto (para pruebas o cuando no se desea anti-rollback).</summary>
public sealed class NullClockGuard : IClockGuard
{
    public bool IsRolledBack(DateTime nowUtc) => false;
    public void Remember(DateTime nowUtc) { }
}

/// <summary>
/// Anti-rollback persistente. Guarda la última fecha vista, cifrada con AES usando una clave
/// derivada del HWID, en un archivo oculto de datos de la aplicación. Si el reloj del sistema
/// retrocede respecto de ese valor, se detecta la manipulación.
///
/// Que la clave dependa del HWID evita que copiar el archivo a otra PC sirva de algo.
/// En producción conviene además escribir una segunda copia (p.ej. en el registro de Windows)
/// para que borrar el archivo no baste; la interfaz permite añadir esa capa sin cambiar el resto.
/// </summary>
public sealed class FileClockGuard : IClockGuard
{
    private readonly string _path;
    private readonly byte[] _key;
    private readonly TimeSpan _tolerance;

    public FileClockGuard(string hardwareId, string? filePath = null, TimeSpan? tolerance = null)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("SIMBER-CLOCK|" + hardwareId));
        _tolerance = tolerance ?? TimeSpan.FromHours(12);
        _path = filePath ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimberDesigns");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ".sdstate");
    }

    public bool IsRolledBack(DateTime nowUtc)
    {
        var last = ReadLastSeen();
        if (last is null) return false;
        return nowUtc.ToUniversalTime() < last.Value - _tolerance;
    }

    public void Remember(DateTime nowUtc)
    {
        var last = ReadLastSeen();
        var toStore = last is null ? nowUtc.ToUniversalTime()
                                   : (nowUtc.ToUniversalTime() > last.Value ? nowUtc.ToUniversalTime() : last.Value);
        WriteLastSeen(toStore);
    }

    private DateTime? ReadLastSeen()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            byte[] enc = File.ReadAllBytes(_path);
            byte[] plain = Decrypt(enc);
            long ticks = BitConverter.ToInt64(plain, 0);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch
        {
            return null; // Archivo corrupto/borrado: se trata como "sin registro".
        }
    }

    private void WriteLastSeen(DateTime value)
    {
        byte[] plain = BitConverter.GetBytes(value.ToUniversalTime().Ticks);
        byte[] enc = Encrypt(plain);

        // Si el archivo ya existe y quedó OCULTO de una escritura previa, hay que quitar ese
        // atributo antes de sobrescribir; de lo contrario Windows lanza UnauthorizedAccessException.
        if (File.Exists(_path))
        {
            try { File.SetAttributes(_path, FileAttributes.Normal); } catch { /* ignore */ }
        }

        try
        {
            File.WriteAllBytes(_path, enc);
            try { File.SetAttributes(_path, FileAttributes.Hidden); } catch { /* best effort */ }
        }
        catch (UnauthorizedAccessException)
        {
            // El anti-rollback es una capa extra: si no se puede escribir, no debe tumbar la app.
        }
        catch (IOException)
        {
            // idem: nunca fallar el arranque por el archivo de estado.
        }
    }

    private byte[] Encrypt(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        byte[] body = enc.TransformFinalBlock(plain, 0, plain.Length);
        byte[] result = new byte[aes.IV.Length + body.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(body, 0, result, aes.IV.Length, body.Length);
        return result;
    }

    private byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        byte[] iv = new byte[16];
        Buffer.BlockCopy(data, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 16, data.Length - 16);
    }
}
