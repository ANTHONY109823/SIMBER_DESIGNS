using Pgvector;

namespace SimberDesigns.Server.Models;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.Customer;
    public decimal CreditsBalance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Tier { get; set; } = MembershipTiers.Basic;
    public string Status { get; set; } = SubscriptionStatuses.Active;
    public int DailyDownloadLimit { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? ExternalSubId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CreditPackage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal CreditsAmount { get; set; }
    public decimal BonusAmount { get; set; }
    public decimal PriceUsd { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Design
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = "Jersey";
    public decimal PriceUsd { get; set; }
    public decimal CreditsCost { get; set; }
    public string R2Key { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public bool IsFreeDaily { get; set; } = true;
    public Vector? Embedding { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Transaction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Gateway { get; set; } = Gateways.LemonSqueezy;
    public string Status { get; set; } = TransactionStatuses.Pending;
    public string? ExternalReferenceId { get; set; }
    public string? PaymentReceiptUrl { get; set; }
    public Guid? CreditPackageId { get; set; }
    public CreditPackage? CreditPackage { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public Guid? DesignId { get; set; }
    public Design? Design { get; set; }
    public string? Notes { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CreditTransaction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? TransactionId { get; set; }
    public Guid? DesignId { get; set; }
    public Design? Design { get; set; }
    public decimal CreditsChanged { get; set; }
    public string TxType { get; set; } = CreditTxTypes.Recharge;
    public DateTime CreatedAt { get; set; }
}

public sealed class UserDownload
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid DesignId { get; set; }
    public Design Design { get; set; } = null!;
    public DateTime DownloadedAt { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}

public sealed class PluginLicense
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string? HardwareId { get; set; }
    public string Plan { get; set; } = PluginPlans.Month1Pc;
    public string Status { get; set; } = PluginLicenseStatuses.Active;
    public string ActivationCode { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
