using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

/// <summary>Precios de los planes, editables desde el admin (guardados en SiteContents) con
/// respaldo a la config. El monto siempre se calcula en el servidor con estos valores.</summary>
public sealed record PlanPricing(decimal MonthUsd, decimal MonthPen, int Discount3, int Discount6)
{
    public int DiscountFor(int months) => months switch { 3 => Discount3, 6 => Discount6, _ => 0 };
}

public interface IPlanPricingService
{
    Task<PlanPricing> GetAsync(CancellationToken cancellationToken);
}

public sealed class PlanPricingService(AppDbContext db, IOptions<MercadoPagoOptions> options) : IPlanPricingService
{
    public const string KeyMonthUsd = "plan.month_usd";
    public const string KeyUsdToPen = "plan.usd_to_pen";
    public const string KeyDiscount3 = "plan.discount3";
    public const string KeyDiscount6 = "plan.discount6";

    public async Task<PlanPricing> GetAsync(CancellationToken cancellationToken)
    {
        string[] keys = [KeyMonthUsd, KeyUsdToPen, KeyDiscount3, KeyDiscount6];
        var map = await db.SiteContents.AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, cancellationToken);

        var opt = options.Value;
        var monthUsd = ReadDecimal(map, KeyMonthUsd, opt.PluginMonthPriceUsd);
        var usdToPen = ReadDecimal(map, KeyUsdToPen, opt.UsdToPenRate);
        var disc3 = ReadDiscount(map, KeyDiscount3, 10);
        var disc6 = ReadDiscount(map, KeyDiscount6, 20);

        if (monthUsd <= 0) monthUsd = opt.PluginMonthPriceUsd;
        if (usdToPen <= 0) usdToPen = opt.UsdToPenRate;

        var monthPen = Math.Round(monthUsd * usdToPen, 0, MidpointRounding.AwayFromZero);
        return new PlanPricing(monthUsd, monthPen, disc3, disc6);
    }

    private static decimal ReadDecimal(IReadOnlyDictionary<string, string> map, string key, decimal fallback)
        => map.TryGetValue(key, out var v)
           && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
           && d > 0
            ? d
            : fallback;

    private static int ReadDiscount(IReadOnlyDictionary<string, string> map, string key, int fallback)
        => map.TryGetValue(key, out var v) && int.TryParse(v, out var d)
            ? Math.Clamp(d, 0, 90)
            : fallback;
}
