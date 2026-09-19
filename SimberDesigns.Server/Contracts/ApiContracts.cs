namespace SimberDesigns.Server.Contracts;

public sealed record RegisterRequest(string Email, string Password, string FullName);
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResponse(
    string Token,
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    string? SubscriptionTier,
    int DailyLimit,
    int DownloadsToday,
    bool MustChangePassword = false);

public sealed record DesignDto(
    Guid Id,
    string Title,
    string Slug,
    string? Description,
    string Category,
    decimal PriceUsd,
    decimal CreditsCost,
    string PreviewUrl,
    DateTime CreatedAt,
    bool IsFreeDaily,
    float? Similarity);

public sealed record DownloadResponse(string DownloadUrl, DateTime ExpiresAt, int RemainingToday);

public sealed record PresignedUrlResponse(string UploadUrl, string FileKey);

public sealed record CheckoutRequest(string? PlanKey, string? PackKey, string? Kind, Guid? PackageId, int? Months);
public sealed record CheckoutResponse(string CheckoutUrl, Guid TransactionId, bool FakeCheckout);
public sealed record ConfirmPaymentRequest(Guid? TransactionId, string? PaymentId, string? ExternalReference);
public sealed record PaymentConfigDto(string PublicKey, bool CardEnabled);
public sealed record CardPaymentRequest(
    string? Kind,
    int? Months,
    Guid? PackageId,
    string Token,
    string PaymentMethodId,
    string? IssuerId,
    int Installments,
    string? PayerEmail);
public sealed record CardPaymentResponse(string Status, string Message, Guid TransactionId, string? Serial = null);
public sealed record AssignEditionRequest(string Code, string Edition);
public sealed record PluginPlanOptionDto(int Months, int Days, decimal PricePen, string Label);
public sealed record StorefrontDto(
    IReadOnlyList<CreditPackageDto> Packages,
    decimal PluginMonthPricePen,
    string PluginPlanName,
    bool HasCorelDownload,
    bool HasIllustratorDownload,
    IReadOnlyList<PluginPlanOptionDto>? PluginPlans = null);
public sealed record PluginLicenseDto(
    string Plan,
    string Status,
    string ActivationCode,
    DateTime ExpiresAt,
    bool IsActive,
    string? HardwareId,
    string Edition);
public sealed record PluginPeriodKeyDto(
    Guid Id,
    string Code,
    string Edition,
    int Days,
    string Status,
    string Source,
    string? Note,
    DateTime CreatedAt,
    DateTime? RedeemedAt);

// El .exe manda su HWID y el programa (Corel / Illustrator); el servidor devuelve el token FIRMADO.
public sealed record PluginActivateRequest(string HardwareId, string? Edition);
public sealed record PluginRedeemRequest(string Code, string? HardwareId, string? Edition);
public sealed record PluginInstallerStatusDto(
    string Edition,
    bool Ready,
    string? FileName,
    long SizeBytes,
    DateTime? UpdatedAt);
public sealed record PluginTokenDto(string Token, DateTime ExpiresAt, string Plan, string Edition);

// ---- Panel de administración ----
public sealed record AdminMetricsDto(
    int TotalUsers,
    int ActiveLicenses,
    int ExpiredLicenses,
    int SalesThisMonth,
    decimal RevenueThisMonth,
    decimal TotalRevenue,
    int PaidPayments);

public sealed record AdminLicenseDto(
    string CustomerEmail,
    string CustomerName,
    string Plan,
    string Status,
    bool IsActive,
    DateTime ExpiresAt,
    string? HardwareId,
    string ActivationCode,
    DateTime CreatedAt,
    string Edition);

public sealed record AdminPeriodKeyDto(
    Guid Id,
    string CustomerEmail,
    string CustomerName,
    string Code,
    string Edition,
    int Days,
    string Status,
    string Source,
    string? Note,
    DateTime CreatedAt,
    DateTime? RedeemedAt);

public sealed record AdminCustomerDto(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    int ActiveLicenses,
    DateTime CreatedAt);

public sealed record AdjustCreditsRequest(decimal Credits, string? Note);
public sealed record AdminCreateCustomerRequest(
    string Email,
    string FullName,
    string? TemporaryPassword,
    bool MustChangePassword,
    string? Edition,
    int? Days,
    string? Note);
public sealed record AdminCreateCustomerResponse(
    Guid UserId,
    string Email,
    string TemporaryPassword,
    PluginPeriodKeyDto? PeriodKey);
public sealed record AdminIssuePeriodKeyRequest(
    Guid? UserId,
    string? Email,
    string Edition,
    int Days,
    string? Note);

public sealed record CreditPackageDto(Guid Id, string Name, decimal CreditsAmount, decimal BonusAmount, decimal PriceUsd);

public sealed record AccountDashboardDto(
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    string? SubscriptionTier,
    string? SubscriptionStatus,
    DateTime? SubscriptionExpiresAt,
    int DailyLimit,
    int DownloadsToday,
    IReadOnlyList<UnlockedDesignDto> UnlockedDesigns,
    IReadOnlyList<TransactionDto> Transactions,
    IReadOnlyList<CreditLogDto> CreditLogs);

public sealed record UnlockedDesignDto(Guid Id, string Title, string Slug, string PreviewUrl, bool UnlockedViaSubscription);

public sealed record TransactionDto(
    Guid Id,
    string DetailName,
    decimal Amount,
    string Currency,
    string Gateway,
    string Status,
    string? ExternalReferenceId,
    string? PaymentReceiptUrl,
    string? Notes,
    DateTime CreatedAt);

public sealed record CreditLogDto(Guid Id, string Description, decimal CreditsChanged, DateTime CreatedAt);

