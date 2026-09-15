using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Options;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PaymentsController(
    AppDbContext db,
    IMercadoPagoService mercadoPago,
    IPaymentFulfillmentService fulfillment,
    IOptions<MercadoPagoOptions> mercadoPagoOptions,
    PluginInstallerStorage installers) : ControllerBase
{
    [HttpGet("storefront")]
    [AllowAnonymous]
    public async Task<ActionResult<StorefrontDto>> Storefront(CancellationToken cancellationToken)
    {
        var items = await db.CreditPackages
            .AsNoTracking()
            .Where(p => p.Active)
            .OrderBy(p => p.PriceUsd)
            .Select(p => new CreditPackageDto(p.Id, p.Name, p.CreditsAmount, p.BonusAmount, p.PriceUsd))
            .ToListAsync(cancellationToken);
        return Ok(new StorefrontDto(
            items,
            mercadoPagoOptions.Value.PluginMonthPricePen,
            "Activación 30 días · US$15 por programa",
            installers.Exists(LicenseProgram.Corel),
            installers.Exists(LicenseProgram.Illustrator)));
    }

    [Authorize]
    [HttpPost("checkout")]
    public async Task<ActionResult<CheckoutResponse>> Checkout(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Unauthorized();
        }

        var kind = (request.Kind ?? request.PlanKey ?? request.PackKey ?? "").Trim().ToLowerInvariant();
        CreditPackage? package = null;
        decimal amount;
        string title;
        string notes;
        string currency = "PEN";

        if (kind is "plugin-corel" or "corel")
        {
            amount = mercadoPagoOptions.Value.PluginMonthPricePen;
            title = "Activación 30 días · CorelDRAW";
            notes = $"plugin:{PluginPlans.Month1Pc}:{LicenseProgram.Corel}";
        }
        else if (kind is "plugin-illustrator" or "plugin-ilus" or "illustrator" or "ilus")
        {
            amount = mercadoPagoOptions.Value.PluginMonthPricePen;
            title = "Activación 30 días · Illustrator";
            notes = $"plugin:{PluginPlans.Month1Pc}:{LicenseProgram.Illustrator}";
        }
        else if (kind is "plugin" or "month-1pc" or PluginPlans.Month1Pc)
        {
            return BadRequest("Elige CorelDRAW o Illustrator. Cada programa se activa un mes por separado.");
        }
        else
        {
            if (request.PackageId is Guid packageId)
            {
                package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Id == packageId && p.Active, cancellationToken);
            }
            else
            {
                var packName = ResolvePackageName(request.PackKey ?? request.Kind);
                if (packName is not null)
                {
                    package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Name == packName && p.Active, cancellationToken);
                }
            }

            if (package is null)
            {
                return BadRequest("Elige un paquete de créditos o el mes del plugin.");
            }

            amount = package.PriceUsd;
            title = $"Créditos Simber · {package.CreditsAmount + package.BonusAmount:0}";
            notes = $"credits:{package.Id}";
        }

        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Amount = amount,
            Currency = currency,
            Gateway = Gateways.MercadoPago,
            Status = TransactionStatuses.Pending,
            CreditPackageId = package?.Id,
            Notes = notes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync(cancellationToken);

        var origin = PublicOrigin();
        if (mercadoPago.UseFakeCheckout)
        {
            return Ok(new CheckoutResponse($"{origin}/pago/ok?tx={tx.Id}&fake=1", tx.Id, true));
        }

        try
        {
            var preference = await mercadoPago.CreatePreferenceAsync(
                title,
                amount,
                user.Email,
                tx.Id.ToString(),
                $"{origin}/api/webhooks/mercadopago",
                $"{origin}/pago/ok",
                $"{origin}/pago/error",
                $"{origin}/pago/pendiente",
                cancellationToken);
            tx.ExternalReferenceId = preference.PreferenceId;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new CheckoutResponse(preference.CheckoutUrl, tx.Id, false));
        }
        catch (Exception ex)
        {
            tx.Status = TransactionStatuses.Rejected;
            tx.Notes = $"{notes}|checkout_error:{ex.Message}";
            tx.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return BadRequest(CheckoutErrorMessage(ex.Message));
        }
    }

    private static string CheckoutErrorMessage(string detail)
    {
        if (detail.Contains("PA_UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("PolicyAgent", StringComparison.OrdinalIgnoreCase))
        {
            return "Mercado Pago bloqueó las claves de esta cuenta (PA_UNAUTHORIZED). "
                + "Verifica la identidad del vendedor en Mercado Pago, regenera el Access Token "
                + "y actualiza MercadoPago__AccessToken en Railway. Detalle: " + detail;
        }

        return "No se pudo abrir Mercado Pago. Revisa Access Token y que MercadoPago__PublicBaseUrl sea https://… (" + detail + ")";
    }

    [Authorize]
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(ConfirmPaymentRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        Transaction? tx = null;
        if (request.TransactionId is Guid txId)
        {
            tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == txId && t.UserId == userId, cancellationToken);
        }
        else if (Guid.TryParse(request.ExternalReference, out var extId))
        {
            tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == extId && t.UserId == userId, cancellationToken);
        }

        if (tx is null)
        {
            return NotFound("No encontramos esa orden.");
        }

        if (tx.Status == TransactionStatuses.Completed)
        {
            return Ok(new { message = "Pago ya acreditado.", transactionId = tx.Id });
        }

        if (mercadoPago.UseFakeCheckout)
        {
            await fulfillment.FulfillAsync(tx, request.PaymentId ?? "fake-local", cancellationToken);
            return Ok(new { message = "Pago de prueba acreditado.", transactionId = tx.Id });
        }

        if (string.IsNullOrWhiteSpace(request.PaymentId))
        {
            return BadRequest("Falta el identificador de pago de Mercado Pago.");
        }

        var payment = await mercadoPago.GetPaymentAsync(request.PaymentId, cancellationToken);
        if (payment is null || !string.Equals(payment.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Mercado Pago aún no confirma este pago.");
        }

        // SEGURIDAD: el pago DEBE corresponder a ESTA orden y cubrir su monto. Sin esto, interceptando
        // con Burp u otro proxy se podría confirmar una orden reutilizando un payment_id ajeno o de menor
        // monto. El external_reference lo fijó el servidor al crear la preferencia (= tx.Id).
        if (!string.Equals(payment.ExternalReference, tx.Id.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("El pago no corresponde a esta orden.");
        }
        if (payment.TransactionAmount + 0.5m < tx.Amount)
        {
            return BadRequest("El monto pagado no cubre esta orden.");
        }

        await fulfillment.FulfillAsync(tx, payment.PaymentId, cancellationToken);
        return Ok(new { message = "Pago acreditado.", transactionId = tx.Id });
    }

    private string PublicOrigin()
    {
        var configured = mercadoPagoOptions.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var baseUrl = configured.Trim().TrimEnd('/');
            if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = "https://" + baseUrl["http://".Length..];
            }

            return baseUrl;
        }

        // Detrás de Railway el esquema suele llegar como http; Mercado Pago exige https
        // en back_urls y notification_url (si no, el checkout falla y queda "en proceso").
        var forwarded = Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var scheme = !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : Request.Scheme;
        var host = Request.Host.Value ?? "localhost";
        if (!host.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("127.", StringComparison.Ordinal)
            && string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "https";
        }

        return $"{scheme}://{host}".TrimEnd('/');
    }

    private static string? ResolvePackageName(string? itemKey) => itemKey switch
    {
        "Pack_Basic" or "Basic" => CreditPackageNames.Basic,
        "Pack_VIP" or "VIP" => CreditPackageNames.Vip,
        "Pack_Elite" or "Elite" => CreditPackageNames.Elite,
        _ => null
    };
}
