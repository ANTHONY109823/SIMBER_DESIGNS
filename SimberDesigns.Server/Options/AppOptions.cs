namespace SimberDesigns.Server.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int ExpiryHours { get; set; } = 12;
}

public sealed class CloudflareR2Options
{
    public const string SectionName = "CloudflareR2";
    /// <summary>true = disco local (dev). false = bucket R2 real (producción).</summary>
    public bool UseFakeClient { get; set; } = true;
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    /// <summary>Ej. https://&lt;ACCOUNT_ID&gt;.r2.cloudflarestorage.com</summary>
    public string ServiceUrl { get; set; } = "";
    /// <summary>Opcional. URL pública del bucket/custom domain solo para previews/CMS (nunca packs).</summary>
    public string PublicBaseUrl { get; set; } = "";
    public int PresignedUrlExpiryMinutes { get; set; } = 10;
    public int PreviewUrlExpiryMinutes { get; set; } = 60;
    public int UploadExpiryMinutes { get; set; } = 20;
}

public sealed class MercadoPagoOptions
{
    public const string SectionName = "MercadoPago";
    public bool UseFakeCheckout { get; set; } = true;
    public bool UseSandbox { get; set; } = true;
    public string AccessToken { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>Precio del mes del plugin en USD (US$12). Mercado Pago cobra en PEN al tipo de cambio.</summary>
    public decimal PluginMonthPriceUsd { get; set; } = 12m;
    /// <summary>Tipo de cambio USD → PEN. Ej. 3.75 ⇒ US$12 = S/45.</summary>
    public decimal UsdToPenRate { get; set; } = 3.75m;
    /// <summary>Override opcional del monto en PEN. Si es 0, se usa USD × tipo de cambio.</summary>
    public decimal PluginMonthPricePen { get; set; } = 0m;

    public decimal ResolvePluginMonthPricePen()
        => PluginMonthPricePen > 0
            ? PluginMonthPricePen
            : Math.Round(PluginMonthPriceUsd * UsdToPenRate, 0, MidpointRounding.AwayFromZero);
}

public sealed class LemonSqueezyOptions
{
    public const string SectionName = "LemonSqueezy";
    public string WebhookSecret { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string CheckoutBaseUrl { get; set; } = "";
    public int BasicVariantId { get; set; } = 456781;
    public int VipVariantId { get; set; } = 456782;
    public int SemestralVariantId { get; set; } = 456783;
    public int CreditsBasicVariantId { get; set; } = 567891;
    public int CreditsVipVariantId { get; set; } = 567892;
    public int CreditsEliteVariantId { get; set; } = 567893;
}

public sealed class OnnxOptions
{
    public const string SectionName = "Onnx";
    public string ModelPath { get; set; } = "";
    public string InputName { get; set; } = "image";
}

/// <summary>Lectura de listas con IA (Claude). La ApiKey vive SOLO en el servidor (env de Railway),
/// nunca en el .exe. El plugin manda la foto + su licencia firmada y el servidor llama a Claude.</summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = "";
    /// <summary>Haiku: más barato; suficiente para listas tipográficas y muchas a mano.</summary>
    public string Model { get; set; } = "claude-haiku-4-5";
    public int MaxTokens { get; set; } = 2048;
    /// <summary>Tope interno por PC/día (HWID). No se muestra al cliente.</summary>
    public int DailyReadsPerPc { get; set; } = 15;
}
