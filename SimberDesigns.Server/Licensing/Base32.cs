using System.Text;

namespace SimberDesigns.Licensing;

/// <summary>
/// Base32 (alfabeto de Crockford, sin caracteres ambiguos I/L/O/U) para códigos legibles
/// que el cliente copia a mano sin confundir 0/O ni 1/I.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                bitsLeft -= 5;
                sb.Append(Alphabet[index]);
            }
        }
        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            sb.Append(Alphabet[index]);
        }
        return sb.ToString();
    }
}
