using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    ICloudflareR2Service r2,
    IMercadoPagoService mercadoPago,
    IPaymentFulfillmentService fulfillment,
    IOptions<MercadoPagoOptions> mercadoPagoOptions) : ControllerBase
{
    [HttpGet("packages")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<CreditPackageDto>>> Packages(CancellationToken cancellationToken)
    {
        var items = await db.CreditPackages
            .AsNoTracking()
            .Where(p => p.Active)
            .OrderBy(p => p.PriceUsd)
            .Select(p => new CreditPackageDto(p.Id, p.Name, p.CreditsAmount, p.BonusAmount, p.PriceUsd))
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

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
        return Ok(new StorefrontDto(items, mercadoPagoOptions.Value.PluginMonthPricePen, "Plugin Premium · 1 PC · 30 días"));
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

        if (kind is "plugin" or "month-1pc" or PluginPlans.Month1Pc)
        {
            amount = mercadoPagoOptions.Value.PluginMonthPricePen;
            title = "Simber Plugin Premium · 30 días";
            notes = $"plugin:{PluginPlans.Month1Pc}";
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

    [Authorize]
    [HttpPost("manual-recharge")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> RequestManualRecharge(
        [FromForm] Guid? creditPackageId,
        [FromForm] string? itemKey,
        [FromForm] string? email,
        [FromForm] string? referenceNumber,
        IFormFile? receiptImage,
        IFormFile? receipt,
        CancellationToken cancellationToken)
    {
        var proof = receiptImage ?? receipt;
        if (proof is null || proof.Length == 0)
        {
            return BadRequest("El comprobante de pago en imagen es obligatorio.");
        }

        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.FindAsync([userId.Value], cancellationToken);
        if (user is null)
        {
            return Unauthorized();
        }

        CreditPackage? package = null;
        if (creditPackageId is Guid packageId)
        {
            package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Id == packageId && p.Active, cancellationToken);
        }
        else
        {
            var packName = ResolvePackageName(itemKey);
            if (packName is not null)
            {
                package = await db.CreditPackages.FirstOrDefaultAsync(p => p.Name == packName && p.Active, cancellationToken);
            }
        }

        var subscriptionTier = ResolveSubscriptionTier(itemKey);
        if (package is null && subscriptionTier is null)
        {
            return BadRequest("Indica un paquete de créditos o una membresía válida.");
        }

        var extension = Path.GetExtension(proof.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var receiptKey = $"receipts/{userId}/{Guid.NewGuid():N}{extension}";
        await using var stream = proof.OpenReadStream();
        await r2.UploadAsync(
            receiptKey,
            stream,
            string.IsNullOrWhiteSpace(proof.ContentType) ? "image/jpeg" : proof.ContentType,
            cancellationToken);

        var amount = package?.PriceUsd ?? subscriptionTier switch
        {
            MembershipTiers.Basic => 11m,
            MembershipTiers.Vip => 16m,
            MembershipTiers.Semestral => 65m,
            _ => 0m
        };

        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Amount = amount,
            Currency = "USD",
            Gateway = Gateways.ManualQr,
            Status = TransactionStatuses.Pending,
            ExternalReferenceId = string.IsNullOrWhiteSpace(referenceNumber) ? email ?? user.Email : referenceNumber,
            PaymentReceiptUrl = receiptKey,
            CreditPackageId = package?.Id,
            Notes = subscriptionTier is null ? itemKey : $"subscription:{subscriptionTier}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            message = "Su solicitud ha sido registrada. Un administrador verificará el comprobante en un plazo de 5 a 30 minutos.",
            transactionId = tx.Id
        });
    }

    [Authorize]
    [HttpGet("mine")]
    public async Task<ActionResult<IReadOnlyList<TransactionDto>>> Mine(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var items = await db.Transactions
            .AsNoTracking()
            .Include(t => t.CreditPackage)
            .Include(t => t.Subscription)
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TransactionDto(
                t.Id,
                t.CreditPackage != null ? $"Paquete {t.CreditPackage.Name}"
                    : t.Subscription != null ? $"Membresía {t.Subscription.Tier}"
                    : t.Notes ?? "Transacción",
                t.Amount,
                t.Currency,
                t.Gateway,
                t.Status,
                t.ExternalReferenceId,
                t.PaymentReceiptUrl,
                t.Notes,
                t.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet("admin")]
    public async Task<ActionResult<IReadOnlyList<AdminTransactionDto>>> Admin(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var query = db.Transactions
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.CreditPackage)
            .Where(t => t.Gateway == Gateways.ManualQr || t.Gateway == Gateways.Transfer)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status == status);
        }

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = items.Select(t =>
        {
            var credits = t.CreditPackage is null
                ? 0m
                : t.CreditPackage.CreditsAmount + t.CreditPackage.BonusAmount;
            var name = t.CreditPackage?.Name is string pack
                ? $"Plan {pack} (Créditos)"
                : t.Notes?.StartsWith("subscription:") == true
                    ? $"Membresía {t.Notes["subscription:".Length..]}"
                    : t.Notes ?? "Pago local";
            return new AdminTransactionDto(
                t.Id,
                t.UserId,
                t.User.FullName,
                t.User.Email,
                t.Amount,
                credits,
                name,
                t.PaymentReceiptUrl,
                t.Status,
                t.Notes,
                t.CreatedAt);
        }).ToList();

        return Ok(result);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<AdminTransactionDto>> Approve(Guid id, CancellationToken cancellationToken)
        => await ReviewAsync(id, TransactionStatuses.Completed, null, cancellationToken);

    [Authorize(Roles = Roles.Admin)]
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<AdminTransactionDto>> Reject(Guid id, ReviewPaymentRequest? request, CancellationToken cancellationToken)
        => await ReviewAsync(id, TransactionStatuses.Rejected, request?.Notes, cancellationToken);

    private async Task<ActionResult<AdminTransactionDto>> ReviewAsync(
        Guid id,
        string status,
        string? notes,
        CancellationToken cancellationToken)
    {
        var adminId = User.GetUserId();
        var tx = await db.Transactions
            .Include(t => t.User)
            .Include(t => t.CreditPackage)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tx is null)
        {
            return NotFound();
        }

        bool yaCompletada = tx.Status == TransactionStatuses.Completed;
        tx.Status = status;
        tx.VerifiedBy = adminId;
        tx.VerifiedAt = DateTime.UtcNow;
        tx.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            tx.Notes = string.IsNullOrWhiteSpace(tx.Notes) ? notes : $"{tx.Notes}\n{notes}";
        }

        if (status == TransactionStatuses.Completed)
        {
            var tier = ParseSubscriptionNote(tx.Notes);
            if (tier is not null)
            {
                var limit = MembershipLimits.ForTier(tier);
                var existing = await db.Subscriptions.FirstOrDefaultAsync(
                    s => s.UserId == tx.UserId && s.Tier == tier, cancellationToken);
                if (existing is null)
                {
                    existing = new Subscription
                    {
                        Id = Guid.NewGuid(),
                        UserId = tx.UserId,
                        Tier = tier,
                        Status = SubscriptionStatuses.Active,
                        DailyDownloadLimit = limit,
                        StartsAt = DateTime.UtcNow,
                        ExpiresAt = tier == MembershipTiers.Semestral
                            ? DateTime.UtcNow.AddMonths(6)
                            : DateTime.UtcNow.AddMonths(1),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    db.Subscriptions.Add(existing);
                }
                else
                {
                    existing.Status = SubscriptionStatuses.Active;
                    existing.DailyDownloadLimit = limit;
                    existing.StartsAt = DateTime.UtcNow;
                    existing.ExpiresAt = tier == MembershipTiers.Semestral
                        ? DateTime.UtcNow.AddMonths(6)
                        : DateTime.UtcNow.AddMonths(1);
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                tx.SubscriptionId = existing.Id;
            }

            // Acreditar créditos del paquete (antes esto NO se hacía al aprobar manualmente).
            if (!yaCompletada && tx.CreditPackageId is not null && tx.CreditPackage is not null)
            {
                decimal total = tx.CreditPackage.CreditsAmount + tx.CreditPackage.BonusAmount;
                tx.User.CreditsBalance += total;
                tx.User.UpdatedAt = DateTime.UtcNow;
                db.CreditTransactions.Add(new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = tx.UserId,
                    TransactionId = tx.Id,
                    CreditsChanged = total,
                    TxType = CreditTxTypes.Recharge,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var credits = tx.CreditPackage is null ? 0m : tx.CreditPackage.CreditsAmount + tx.CreditPackage.BonusAmount;
        return Ok(new AdminTransactionDto(
            tx.Id,
            tx.UserId,
            tx.User.FullName,
            tx.User.Email,
            tx.Amount,
            credits,
            tx.CreditPackage?.Name ?? tx.Notes ?? "Pago local",
            tx.PaymentReceiptUrl,
            tx.Status,
            tx.Notes,
            tx.CreatedAt));
    }

    private string PublicOrigin()
    {
        var configured = mercadoPagoOptions.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}";
    }

    private static int? ResolveVariantId(CheckoutRequest request, LemonSqueezyOptions lemon)
    {
        var key = request.PlanKey ?? request.PackKey ?? "";
        return key switch
        {
            "Basic_Monthly" or "Basic" => lemon.BasicVariantId,
            "VIP_Monthly" or "VIP" => lemon.VipVariantId,
            "Semestral_6Month" or "Semestral" => lemon.SemestralVariantId,
            "Pack_Basic" => lemon.CreditsBasicVariantId,
            "Pack_VIP" => lemon.CreditsVipVariantId,
            "Pack_Elite" => lemon.CreditsEliteVariantId,
            _ => null
        };
    }

    private static string? ResolvePackageName(string? itemKey) => itemKey switch
    {
        "Pack_Basic" or "Basic" => CreditPackageNames.Basic,
        "Pack_VIP" or "VIP" => CreditPackageNames.Vip,
        "Pack_Elite" or "Elite" => CreditPackageNames.Elite,
        _ => null
    };

    private static string? ResolveSubscriptionTier(string? itemKey) => itemKey switch
    {
        "Basic_Monthly" => MembershipTiers.Basic,
        "VIP_Monthly" => MembershipTiers.Vip,
        "Semestral_6Month" => MembershipTiers.Semestral,
        _ => null
    };

    private static string? ParseSubscriptionNote(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        const string prefix = "subscription:";
        var line = notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[prefix.Length..].Trim();
    }
}
