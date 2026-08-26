namespace SimberDesigns.Server.Contracts;

public sealed record RegisterRequest(string Email, string Password, string FullName);
public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResponse(
    string Token,
    string Email,
    string FullName,
    string Role,
    decimal CreditsBalance,
    string? SubscriptionTier,
    int DailyLimit,
    int DownloadsToday);

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
    float? Similarity);

public sealed record DownloadResponse(string DownloadUrl, DateTime ExpiresAt, int RemainingToday);

public sealed record PresignedUrlResponse(string UploadUrl, string FileKey);

public sealed record CheckoutRequest(string? PlanKey, string? PackKey);
public sealed record CheckoutResponse(string CheckoutUrl);

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

public sealed record AdminTransactionDto(
    Guid Id,
    Guid UserId,
    string UserName,
    string UserEmail,
    decimal Amount,
    decimal CreditsToReceive,
    string PackageName,
    string? PaymentReceiptUrl,
    string Status,
    string? Notes,
    DateTime CreatedAt);

public sealed record ReviewPaymentRequest(string? Notes);
