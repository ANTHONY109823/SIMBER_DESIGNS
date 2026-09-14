namespace SimberDesigns.Server.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";
    public const string Designer = "Designer";
}

public static class MembershipTiers
{
    public const string Basic = "Basic";
    public const string Vip = "VIP";
    public const string Semestral = "Semestral";
}

public static class SubscriptionStatuses
{
    public const string Active = "Active";
    public const string Canceled = "Canceled";
    public const string Expired = "Expired";
}

public static class Gateways
{
    public const string LemonSqueezy = "LemonSqueezy";
    public const string MercadoPago = "MercadoPago";
    public const string PayPal = "PayPal";
    public const string ManualQr = "Manual_QR";
    public const string Transfer = "Transfer";
}

public static class PluginPlans
{
    public const string Month1Pc = "month-1pc";
}

public static class PluginLicenseStatuses
{
    public const string Active = "Active";
    public const string Expired = "Expired";
}

public static class TransactionStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Refunded = "Refunded";
    public const string Rejected = "Rejected";
}

public static class CreditTxTypes
{
    public const string Recharge = "Recharge";
    public const string PurchaseDesign = "Purchase_Design";
    public const string AiRead = "IA_Read";
    public const string Refund = "Refund";
}

public static class CreditPackageNames
{
    public const string Basic = "Basic";
    public const string Vip = "VIP";
    public const string Elite = "Elite";
}
