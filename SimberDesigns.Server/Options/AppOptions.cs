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
    public bool UseFakeClient { get; set; } = true;
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string ServiceUrl { get; set; } = "";
    public int PresignedUrlExpiryMinutes { get; set; } = 10;
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
    public decimal PluginMonthPricePen { get; set; } = 149;
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
    public string Model { get; set; } = "claude-sonnet-5";
    public int MaxTokens { get; set; } = 4096;
}
