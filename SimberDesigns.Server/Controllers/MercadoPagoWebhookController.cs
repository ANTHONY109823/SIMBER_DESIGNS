using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/webhooks/mercadopago")]
public sealed class MercadoPagoWebhookController(
    AppDbContext db,
    IMercadoPagoService mercadoPago,
    IPaymentFulfillmentService fulfillment,
    ILogger<MercadoPagoWebhookController> logger) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        var dataId = Request.Query["data.id"].ToString();
        if (string.IsNullOrWhiteSpace(dataId))
        {
            dataId = Request.Query["id"].ToString();
        }

        if (string.IsNullOrWhiteSpace(dataId))
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(cancellationToken);
            Request.Body.Position = 0;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.TryGetProperty("id", out var idEl))
                    {
                        dataId = idEl.ToString();
                    }
                }
                catch (JsonException)
                {
                    return Ok();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(dataId))
        {
            return Ok();
        }

        var signature = Request.Headers["x-signature"].ToString();
        var requestId = Request.Headers["x-request-id"].ToString();
        if (!mercadoPago.IsValidWebhook(signature, requestId, dataId))
        {
            logger.LogWarning("Webhook Mercado Pago rechazado: firma inválida.");
            return Unauthorized();
        }

        if (mercadoPago.UseFakeCheckout)
        {
            return Ok();
        }

        var payment = await mercadoPago.GetPaymentAsync(dataId, cancellationToken);
        if (payment is null || !string.Equals(payment.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        if (!Guid.TryParse(payment.ExternalReference, out var txId))
        {
            return Ok();
        }

        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == txId, cancellationToken);
        if (tx is null)
        {
            return Ok();
        }

        await fulfillment.FulfillAsync(tx, payment.PaymentId, cancellationToken);
        return Ok();
    }
}
