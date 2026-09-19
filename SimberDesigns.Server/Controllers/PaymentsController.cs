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
        var monthPen = mercadoPagoOptions.Value.ResolvePluginMonthPricePen();
        var plans = PluginCheckoutPlans.Build(monthPen);
        return Ok(new StorefrontDto(
            items,
            monthPen,
            "Activación por periodo · US$15/mes por programa",
            await installers.ExistsAsync(LicenseProgram.Corel, cancellationToken),
            await installers.ExistsAsync(LicenseProgram.Illustrator, cancellationToken),
            plans));
    }

    [Authorize(Roles = Roles.Customer)]
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

        var charge = await ResolveChargeAsync(
            request.Kind ?? request.PlanKey ?? request.PackKey,
            request.Months,
            request.PackageId,
            request.PackKey ?? request.Kind,
            cancellationToken);
        if (!charge.Ok)
        {
            return BadRequest(charge.Error);
        }

        var package = charge.Package;
        var amount = charge.Amount;
        var title = charge.Title;
        var notes = charge.Notes;
        const string currency = "PEN";

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

    [Authorize(Roles = Roles.Customer)]
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

    // ——— Pago con TARJETA en la misma web (Checkout Bricks) ———

    [HttpGet("config")]
    [AllowAnonymous]
    public ActionResult<PaymentConfigDto> Config()
    {
        var pk = mercadoPago.PublicKey;
        // La tarjeta embebida solo aplica en producción real (token APP_USR-, no fake) y con Public Key.
        var cardEnabled = !mercadoPago.UseFakeCheckout && !string.IsNullOrWhiteSpace(pk);
        return Ok(new PaymentConfigDto(pk, cardEnabled));
    }

    [Authorize(Roles = Roles.Customer)]
    [HttpPost("card")]
    public async Task<ActionResult<CardPaymentResponse>> Card(CardPaymentRequest request, CancellationToken cancellationToken)
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

        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.PaymentMethodId))
        {
            return BadRequest("Faltan datos de la tarjeta.");
        }

        var charge = await ResolveChargeAsync(
            request.Kind,
            request.Months,
            request.PackageId,
            request.Kind,
            cancellationToken);
        if (!charge.Ok)
        {
            return BadRequest(charge.Error);
        }

        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Amount = charge.Amount,
            Currency = "PEN",
            Gateway = Gateways.MercadoPago,
            Status = TransactionStatuses.Pending,
            CreditPackageId = charge.Package?.Id,
            Notes = charge.Notes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync(cancellationToken);

        var origin = PublicOrigin();

        // Modo prueba local (sin token real): acreditamos directo para poder probar el flujo.
        if (mercadoPago.UseFakeCheckout)
        {
            await fulfillment.FulfillAsync(tx, "fake-card", cancellationToken);
            return Ok(new CardPaymentResponse("approved", "Pago de prueba acreditado.", tx.Id));
        }

        MercadoPagoCardResult result;
        try
        {
            result = await mercadoPago.CreateCardPaymentAsync(
                charge.Amount,
                request.Token,
                request.PaymentMethodId,
                request.IssuerId,
                request.Installments,
                string.IsNullOrWhiteSpace(request.PayerEmail) ? user.Email : request.PayerEmail!,
                charge.Title,
                tx.Id.ToString(),
                $"{origin}/api/webhooks/mercadopago",
                cancellationToken);
        }
        catch (Exception ex)
        {
            tx.Status = TransactionStatuses.Rejected;
            tx.Notes = $"{charge.Notes}|card_error:{ex.Message}";
            tx.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return BadRequest("No se pudo procesar la tarjeta. Intenta de nuevo.");
        }

        if (!string.IsNullOrWhiteSpace(result.PaymentId))
        {
            tx.ExternalReferenceId = result.PaymentId;
        }

        if (string.Equals(result.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            // SEGURIDAD: el monto lo fijó el servidor (charge.Amount); igual verificamos que MP haya
            // cobrado al menos ese monto antes de acreditar.
            if (result.TransactionAmount + 0.5m < tx.Amount)
            {
                tx.Status = TransactionStatuses.Rejected;
                tx.Notes = $"{charge.Notes}|monto_insuficiente";
                tx.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return BadRequest("El monto pagado no cubre esta orden.");
            }

            await fulfillment.FulfillAsync(tx, result.PaymentId ?? "mp-card", cancellationToken);
            return Ok(new CardPaymentResponse("approved", "¡Pago aprobado! Tu clave ya está en Mi cuenta.", tx.Id));
        }

        if (string.Equals(result.Status, "in_process", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            // No acreditamos aún: el webhook lo confirmará cuando MP apruebe.
            await db.SaveChangesAsync(cancellationToken);
            return Ok(new CardPaymentResponse("pending", "Tu pago está en revisión. Te avisaremos cuando se apruebe.", tx.Id));
        }

        tx.Status = TransactionStatuses.Rejected;
        tx.Notes = $"{charge.Notes}|rechazado:{result.StatusDetail ?? result.Error ?? result.Status}";
        tx.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new CardPaymentResponse("rejected", RejectMessage(result.StatusDetail), tx.Id));
    }

    private static string RejectMessage(string? statusDetail) => statusDetail switch
    {
        "cc_rejected_insufficient_amount" => "Tarjeta sin fondos suficientes.",
        "cc_rejected_bad_filled_card_number" => "Revisa el número de la tarjeta.",
        "cc_rejected_bad_filled_date" => "Revisa la fecha de vencimiento.",
        "cc_rejected_bad_filled_security_code" => "Revisa el código de seguridad (CVV).",
        "cc_rejected_bad_filled_other" => "Revisa los datos de la tarjeta.",
        "cc_rejected_call_for_authorize" => "Autoriza el pago con tu banco e intenta de nuevo.",
        "cc_rejected_card_disabled" => "Activa tu tarjeta con el banco e intenta de nuevo.",
        "cc_rejected_high_risk" => "El pago fue rechazado. Prueba con otra tarjeta.",
        _ => "El pago fue rechazado. Prueba con otra tarjeta o medio de pago."
    };

    /// <summary>
    /// Resuelve el cargo (monto, título, notas) SIEMPRE en el servidor a partir del plan/paquete elegido.
    /// El cliente nunca fija el precio. Lo usan tanto el checkout redirigido como el pago con tarjeta.
    /// </summary>
    private async Task<ChargeResolution> ResolveChargeAsync(
        string? rawKind,
        int? months,
        Guid? packageId,
        string? packKey,
        CancellationToken cancellationToken)
    {
        var kind = (rawKind ?? "").Trim().ToLowerInvariant();
        var pluginPen = mercadoPagoOptions.Value.ResolvePluginMonthPricePen();

        if (PluginCheckoutPlans.TryResolve(kind, months, pluginPen, out var edition, out var days, out _, out var amount, out var title))
        {
            return ChargeResolution.Success(amount, title, $"plugin:{days}:{edition}", null);
        }

        if (kind is "plugin" or "month-1pc" or PluginPlans.Month1Pc)
        {
            return ChargeResolution.Fail("Elige CorelDRAW o Illustrator. Cada programa se activa por separado (1, 3, 6 o 12 meses).");
        }

        CreditPackage? package = null;
        if (packageId is Guid pkgId)
        {
            package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Id == pkgId && p.Active, cancellationToken);
        }
        else
        {
            var packName = ResolvePackageName(packKey);
            if (packName is not null)
            {
                package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Name == packName && p.Active, cancellationToken);
            }
        }

        if (package is null)
        {
            return ChargeResolution.Fail("Elige un paquete de créditos o un plan del plugin.");
        }

        return ChargeResolution.Success(
            package.PriceUsd,
            $"Créditos Simber · {package.CreditsAmount + package.BonusAmount:0}",
            $"credits:{package.Id}",
            package);
    }

    private readonly record struct ChargeResolution(
        bool Ok, string? Error, decimal Amount, string Title, string Notes, CreditPackage? Package)
    {
        public static ChargeResolution Success(decimal amount, string title, string notes, CreditPackage? package)
            => new(true, null, amount, title, notes, package);
        public static ChargeResolution Fail(string error)
            => new(false, error, 0, "", "", null);
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
