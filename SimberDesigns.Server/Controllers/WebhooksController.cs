using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Options;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

public sealed class LemonSqueezyPayload
{
    [JsonPropertyName("meta")]
    public WebhookMeta Meta { get; set; } = null!;

    [JsonPropertyName("data")]
    public WebhookData Data { get; set; } = null!;
}

public sealed class WebhookMeta
{
    [JsonPropertyName("event_name")]
    public string EventName { get; set; } = "";

    [JsonPropertyName("custom_data")]
    public Dictionary<string, string>? CustomData { get; set; }
}

public sealed class WebhookData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("attributes")]
    public WebhookAttributes Attributes { get; set; } = null!;
}

public sealed class WebhookAttributes
{
    [JsonPropertyName("store_id")]
    public int StoreId { get; set; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("variant_id")]
    public int VariantId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("renews_at")]
    public DateTime? RenewsAt { get; set; }

    [JsonPropertyName("ends_at")]
    public DateTime? EndsAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("first_order_item")]
    public OrderItem? FirstOrderItem { get; set; }
}

public sealed class OrderItem
{
    [JsonPropertyName("variant_id")]
    public int VariantId { get; set; }
}

[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
public sealed class WebhooksController(
    IConfiguration configuration,
    ILemonSqueezySignatureVerifier signatures,
    IOptions<LemonSqueezyOptions> options,
    ILogger<WebhooksController> logger) : ControllerBase
{
    [HttpPost("lemonsqueezy")]
    public async Task<IActionResult> HandleWebhook()
    {
        if (!Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
        {
            logger.LogWarning("Falta el encabezado X-Signature.");
            return BadRequest("Encabezado de firma faltante.");
        }

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        if (!signatures.IsValid(options.Value.WebhookSecret, rawBody, signatureHeader.ToString()))
        {
            logger.LogWarning("Webhook Lemon Squeezy rechazado: firma HMAC inválida.");
            return Unauthorized("Firma de webhook inválida.");
        }

        LemonSqueezyPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LemonSqueezyPayload>(rawBody);
            if (payload?.Meta is null || payload.Data is null)
            {
                return BadRequest("Payload malformado.");
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Error al deserializar el payload JSON del webhook.");
            return BadRequest("Formato JSON inválido.");
        }

        var eventName = payload.Meta.EventName;
        if (payload.Meta.CustomData is null
            || !payload.Meta.CustomData.TryGetValue("user_id", out var userIdStr)
            || !Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Evento {Event} sin user_id válido en custom_data.", eventName);
            return BadRequest("Falta el metadato crítico 'user_id' para identificar al cliente.");
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:DefaultConnection.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            switch (eventName)
            {
                case "order_created":
                    await HandleOrderCreatedAsync(connection, payload, userId);
                    break;
                case "subscription_created":
                case "subscription_updated":
                    await HandleSubscriptionUpdatedAsync(connection, payload, userId);
                    break;
                case "subscription_cancelled":
                case "subscription_expired":
                    await HandleSubscriptionCancelledAsync(connection, payload);
                    break;
                default:
                    logger.LogInformation("Evento Lemon Squeezy no manejado: {Event}", eventName);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error procesando la base de datos para el evento {Event}", eventName);
            return StatusCode(StatusCodes.Status500InternalServerError, "Error al procesar base de datos.");
        }

        return Ok();
    }

    private async Task HandleOrderCreatedAsync(NpgsqlConnection connection, LemonSqueezyPayload payload, Guid userId)
    {
        var attributes = payload.Data.Attributes;
        var orderId = payload.Data.Id;
        var txExists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM transactions WHERE external_reference_id = @OrderId AND gateway = 'LemonSqueezy')",
            new { OrderId = orderId });
        if (txExists)
        {
            logger.LogInformation("La orden {OrderId} ya fue procesada.", orderId);
            return;
        }

        var variantId = attributes.VariantId != 0
            ? attributes.VariantId
            : attributes.FirstOrderItem?.VariantId ?? 0;

        if (!CreditsVariantMap(options.Value).TryGetValue(variantId, out var packageName))
        {
            logger.LogWarning("Orden {OrderId} con VariantId {VariantId} no registrado para créditos.", orderId, variantId);
            return;
        }

        var creditPackage = await connection.QueryFirstOrDefaultAsync<(Guid Id, decimal PriceUsd)>(
            "SELECT id, price_usd FROM credit_packages WHERE name = @Name AND active = true",
            new { Name = packageName });
        if (creditPackage.Id == Guid.Empty)
        {
            logger.LogError("El paquete de créditos '{PackageName}' no existe.", packageName);
            return;
        }

        var pricePaid = attributes.Total >= 100 ? attributes.Total / 100m : attributes.Total;
        await connection.ExecuteAsync(
            """
            INSERT INTO transactions (id, user_id, amount, currency, gateway, status, external_reference_id, credit_package_id, created_at, updated_at)
            VALUES (@Id, @UserId, @Amount, @Currency, 'LemonSqueezy', 'Completed', @ExternalRef, @PackageId, NOW(), NOW())
            """,
            new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = pricePaid,
                Currency = attributes.Currency,
                ExternalRef = orderId,
                PackageId = creditPackage.Id
            });

        logger.LogInformation("Orden de créditos {OrderId} procesada. Paquete {PackageName} para {UserId}.", orderId, packageName, userId);
    }

    private async Task HandleSubscriptionUpdatedAsync(NpgsqlConnection connection, LemonSqueezyPayload payload, Guid userId)
    {
        var attributes = payload.Data.Attributes;
        var externalSubId = payload.Data.Id;
        if (!SubscriptionVariantMap(options.Value).TryGetValue(attributes.VariantId, out var tierName))
        {
            logger.LogWarning("VariantId ({VariantId}) de suscripción no mapeado.", attributes.VariantId);
            return;
        }

        var dailyDownloadLimit = MembershipLimits.ForTier(tierName);
        var startsAt = attributes.CreatedAt == default ? DateTime.UtcNow : attributes.CreatedAt;
        var expiresAt = attributes.RenewsAt ?? DateTime.UtcNow.AddMonths(tierName == MembershipTiers.Semestral ? 6 : 1);

        var existingId = await connection.ExecuteScalarAsync<Guid?>(
            """
            SELECT id FROM subscriptions
            WHERE external_sub_id = @SubId OR (user_id = @UserId AND tier = @Tier::subscription_tier)
            LIMIT 1
            """,
            new { SubId = externalSubId, UserId = userId, Tier = tierName });

        Guid subscriptionId;
        if (existingId is Guid found)
        {
            subscriptionId = found;
            await connection.ExecuteAsync(
                """
                UPDATE subscriptions
                SET status = 'Active',
                    daily_download_limit = @Limit,
                    starts_at = @StartsAt,
                    expires_at = @ExpiresAt,
                    external_sub_id = @ExternalId,
                    updated_at = NOW()
                WHERE id = @SubId
                """,
                new { SubId = subscriptionId, Limit = dailyDownloadLimit, StartsAt = startsAt, ExpiresAt = expiresAt, ExternalId = externalSubId });
        }
        else
        {
            subscriptionId = Guid.NewGuid();
            await connection.ExecuteAsync(
                """
                INSERT INTO subscriptions (id, user_id, tier, status, daily_download_limit, starts_at, expires_at, external_sub_id, created_at, updated_at)
                VALUES (@Id, @UserId, @Tier::subscription_tier, 'Active', @Limit, @StartsAt, @ExpiresAt, @SubId, NOW(), NOW())
                """,
                new { Id = subscriptionId, UserId = userId, Tier = tierName, Limit = dailyDownloadLimit, StartsAt = startsAt, ExpiresAt = expiresAt, SubId = externalSubId });
        }

        var pricePaid = attributes.Total >= 100 ? attributes.Total / 100m : attributes.Total;
        var alreadyLogged = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM transactions WHERE external_reference_id = @Ref AND gateway = 'LemonSqueezy')",
            new { Ref = $"{externalSubId}:{attributes.CreatedAt:O}" });
        if (!alreadyLogged)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO transactions (id, user_id, amount, currency, gateway, status, external_reference_id, subscription_id, created_at, updated_at)
                VALUES (@Id, @UserId, @Amount, @Currency, 'LemonSqueezy', 'Completed', @ExternalRef, @SubId, NOW(), NOW())
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Amount = pricePaid,
                    Currency = attributes.Currency,
                    ExternalRef = $"{externalSubId}:{attributes.CreatedAt:O}",
                    SubId = subscriptionId
                });
        }
    }

    private async Task HandleSubscriptionCancelledAsync(NpgsqlConnection connection, LemonSqueezyPayload payload)
    {
        var externalSubId = payload.Data.Id;
        var attributes = payload.Data.Attributes;
        var subId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM subscriptions WHERE external_sub_id = @SubId",
            new { SubId = externalSubId });
        if (subId is null)
        {
            logger.LogWarning("Se intentó cancelar la suscripción externa {SubId} pero no existe.", externalSubId);
            return;
        }

        var expiresAt = attributes.EndsAt ?? DateTime.UtcNow;
        var targetStatus = expiresAt > DateTime.UtcNow && payload.Meta.EventName == "subscription_cancelled"
            ? SubscriptionStatuses.Canceled
            : SubscriptionStatuses.Expired;

        await connection.ExecuteAsync(
            """
            UPDATE subscriptions
            SET status = @Status::subscription_status,
                expires_at = @ExpiresAt,
                daily_download_limit = CASE WHEN @Status = 'Expired' THEN 0 ELSE daily_download_limit END,
                updated_at = NOW()
            WHERE id = @Id
            """,
            new { Id = subId.Value, Status = targetStatus, ExpiresAt = expiresAt });
    }

    private static Dictionary<int, string> SubscriptionVariantMap(LemonSqueezyOptions lemon) => new()
    {
        { lemon.BasicVariantId, MembershipTiers.Basic },
        { lemon.VipVariantId, MembershipTiers.Vip },
        { lemon.SemestralVariantId, MembershipTiers.Semestral }
    };

    private static Dictionary<int, string> CreditsVariantMap(LemonSqueezyOptions lemon) => new()
    {
        { lemon.CreditsBasicVariantId, CreditPackageNames.Basic },
        { lemon.CreditsVipVariantId, CreditPackageNames.Vip },
        { lemon.CreditsEliteVariantId, CreditPackageNames.Elite }
    };
}
