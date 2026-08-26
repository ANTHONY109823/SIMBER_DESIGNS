using SimberDesigns.Server.Services;

namespace SimberDesigns.Tests;

public class SimberDesignsTests
{
    [Fact]
    public void Hmac_acepta_firma_hex_valida()
    {
        const string secret = "dev-webhook-secret";
        const string payload = """{"meta":{"event_name":"order_created"}}""";
        var hex = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret),
            System.Text.Encoding.UTF8.GetBytes(payload)));

        Assert.True(HmacSignature.IsValidSha256Hex(secret, payload, hex));
        Assert.True(HmacSignature.IsValidSha256Hex(secret, payload, "sha256=" + hex));
    }

    [Fact]
    public void Hmac_rechaza_firma_invalida_sin_filtrar_longitud()
    {
        Assert.False(HmacSignature.IsValidSha256Hex("secret", "payload", "deadbeef"));
        Assert.False(HmacSignature.IsValidSha256Hex("secret", "payload", null));
        Assert.False(HmacSignature.IsValidSha256Hex("secret", "payload", "not-hex"));
    }

    [Fact]
    public void Verificador_de_lemon_squeezy_usa_la_misma_rutina_hmac()
    {
        ILemonSqueezySignatureVerifier verifier = new LemonSqueezySignatureVerifier();
        const string secret = "abc";
        const string payload = "body";
        var hex = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret),
            System.Text.Encoding.UTF8.GetBytes(payload)));

        Assert.True(verifier.IsValid(secret, payload, hex.ToLowerInvariant()));
        Assert.False(verifier.IsValid(secret, payload, hex[..^2] + "00"));
    }

    [Fact]
    public void Limite_diario_vip_es_25_y_basica_es_15()
    {
        Assert.Equal(15, MembershipLimits.ForTier("Basic"));
        Assert.Equal(25, MembershipLimits.ForTier("VIP"));
        Assert.Equal(30, MembershipLimits.ForTier("Semestral"));
        Assert.True(MembershipLimits.CanDownload(14, 25));
        Assert.False(MembershipLimits.CanDownload(25, 25));
        Assert.Equal(0, MembershipLimits.Remaining(30, 25));
    }

    [Fact]
    public void Embedding_de_desarrollo_siempre_tiene_512_dimensiones_y_norma_unitaria()
    {
        using var stream = new MemoryStream("simber-clip-fixture"u8.ToArray());
        var vector = OnnxEmbeddingService.HashEmbedding(stream);

        Assert.Equal(OnnxEmbeddingService.ClipDimensions, vector.Length);
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        Assert.InRange(norm, 0.99f, 1.01f);
    }

    [Fact]
    public void NormalizeVector_ShouldReturnVectorWithUnitLength()
    {
        var rawVector = new[] { 3.0f, 4.0f, 0.0f };
        float sum = 0;
        for (var i = 0; i < rawVector.Length; i++)
        {
            sum += rawVector[i] * rawVector[i];
        }

        var norm = (float)Math.Sqrt(sum);
        var normalized = new float[rawVector.Length];
        for (var i = 0; i < rawVector.Length; i++)
        {
            normalized[i] = rawVector[i] / norm;
        }

        float normSum = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            normSum += normalized[i] * normalized[i];
        }

        var finalMagnitude = (float)Math.Sqrt(normSum);
        Assert.Equal(0.6f, normalized[0], precision: 4);
        Assert.Equal(0.8f, normalized[1], precision: 4);
        Assert.Equal(1.0f, finalMagnitude, precision: 4);
    }
}
