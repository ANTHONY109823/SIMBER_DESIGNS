using System.Security.Cryptography;
using System.Text;

namespace SimberDesigns.Server.Services;

public interface ILemonSqueezySignatureVerifier
{
    bool IsValid(string secret, string payload, string? providedHex);
}

public sealed class LemonSqueezySignatureVerifier : ILemonSqueezySignatureVerifier
{
    public bool IsValid(string secret, string payload, string? providedHex)
        => HmacSignature.IsValidSha256Hex(secret, payload, providedHex);
}

public static class HmacSignature
{
    public static bool IsValidSha256Hex(string secret, string payload, string? providedHex)
    {
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));

        if (string.IsNullOrWhiteSpace(providedHex) || !TryParseHex(providedHex, out var provided))
        {
            CryptographicOperations.FixedTimeEquals(computed, computed);
            return false;
        }

        if (provided.Length != computed.Length)
        {
            CryptographicOperations.FixedTimeEquals(computed, computed);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, provided);
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = [];
        var value = hex.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? hex["sha256=".Length..]
            : hex;

        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
