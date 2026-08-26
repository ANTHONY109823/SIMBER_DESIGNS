using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Dapper;
using Npgsql;

namespace SimberDesigns.Api.Controllers
{
    #region Lemon Squeezy Webhook DTOs

    public class LemonSqueezyPayload
    {
        [JsonPropertyName("meta")]
        public WebhookMeta Meta { get; set; } = null!;

        [JsonPropertyName("data")]
        public WebhookData Data { get; set; } = null!;
    }

    public class WebhookMeta
    {
        [JsonPropertyName("event_name")]
        public string EventName { get; set; } = string.Empty;

        [JsonPropertyName("custom_data")]
        public Dictionary<string, string>? CustomData { get; set; }
    }

    public class WebhookData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public WebhookAttributes Attributes { get; set; } = null!;
    }

    public class WebhookAttributes
    {
        [JsonPropertyName("store_id")]
        public int StoreId { get; set; }

        [JsonPropertyName("customer_id")]
        public int CustomerId { get; set; }

        [JsonPropertyName("variant_id")]
        public int VariantId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("total")]
        public decimal Total { get; set; } // Represented as cents in Lemon Squeezy (e.g. 1000 = $10.00)

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

    public class OrderItem
    {
        [JsonPropertyName("variant_id")]
        public int VariantId { get; set; }
    }

    #endregion

    [ApiController]
    [Route("api/webhooks")]
    public class LemonSqueezyWebhookController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _webhookSecret;
        private readonly ILogger<LemonSqueezyWebhookController> _logger;

        // Diccionarios estáticos de mapeo de Variant IDs de Lemon Squeezy a la base de datos de Simber designs.
        // En un entorno real, estos IDs se obtienen del panel de Lemon Squeezy y se configuran en appsettings.json.
        private static readonly Dictionary<int, string> SubscriptionVariantToTierMap = new()
        {
            { 456781, "Basic" },     // Membresía Básica ($11/mes) -> 15 descargas diarias
            { 456782, "VIP" },       // Membresía VIP ($16/mes) -> 25 descargas diarias
            { 456783, "Semestral" }  // Membresía Semestral ($65/6 meses) -> 30 descargas diarias
        };

        private static readonly Dictionary<int, string> CreditsVariantToPackageMap = new()
        {
            { 567891, "Basic" },    // Paquete Básica ($10) -> Recibe 10 créditos
            { 567892, "VIP" },      // Paquete VIP ($25) -> Recibe 30 créditos
            { 567893, "Elite" }     // Paquete Elite ($50) -> Recibe 65 créditos
        };

        public LemonSqueezyWebhookController(
            IConfiguration configuration,
            ILogger<LemonSqueezyWebhookController> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? throw new ArgumentNullException("ConnectionStrings:DefaultConnection");
            _webhookSecret = configuration["LemonSqueezy:WebhookSecret"] 
                ?? throw new ArgumentNullException("LemonSqueezy:WebhookSecret");
            _logger = logger;
        }

        [HttpPost("lemonsqueezy")]
        [Consumes("application/json")]
        public async Task<IActionResult> HandleWebhook()
        {
            // 1. Obtener la firma enviada en los encabezados HTTP
            if (!Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
            {
                _logger.LogWarning("Falta el encabezado X-Signature.");
                return BadRequest("Encabezado de firma faltante.");
            }

            string expectedSignature = signatureHeader.ToString();

            // 2. Leer el cuerpo de la solicitud en bruto para validar la firma HMAC-SHA256
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            string rawBody = await reader.ReadToEndAsync();
            Request.Body.Position = 0; // Restablecer la posición para permitir que el deserializador lo lea

            // 3. Verificar criptográficamente la autenticidad del Payload
            if (!VerifyHmacSignature(rawBody, expectedSignature, _webhookSecret))
            {
                _logger.LogWarning("Intento de webhook detectado con firma inválida.");
                return Unauthorized("Firma de webhook inválida.");
            }

            // 4. Deserializar el payload de Lemon Squeezy
            LemonSqueezyPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LemonSqueezyPayload>(rawBody);
                if (payload == null || payload.Meta == null || payload.Data == null)
                {
                    return BadRequest("Payload malformado.");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error al deserializar el payload JSON del webhook.");
                return BadRequest("Formato JSON inválido.");
            }

            string eventName = payload.Meta.EventName;
            _logger.LogInformation("Procesando evento de Lemon Squeezy: {EventName} para la entidad ID: {EntityId}", eventName, payload.Data.Id);

            // 5. Extraer metadatos de usuario necesarios para vincular el pago
            if (payload.Meta.CustomData == null || !payload.Meta.CustomData.TryGetValue("user_id", out var userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                _logger.LogWarning("Evento {Event} recibido sin una ID de usuario ('user_id') válida en CustomData.", eventName);
                return BadRequest("Falta el metadato crítico 'user_id' para identificar al cliente.");
            }

            // 6. Enrutar el evento al manejador específico de lógica de negocio
            using var connection = new NpgsqlConnection(_connectionString);
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
                        await HandleSubscriptionCancelledAsync(connection, payload, userId);
                        break;

                    default:
                        _logger.LogInformation("Evento de Lemon Squeezy no manejado de forma activa: {Event}", eventName);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico procesando la base de datos para el evento {Event}", eventName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error al procesar base de datos.");
            }

            // Lemon Squeezy espera una respuesta HTTP 200 de confirmación rápida
            return Ok();
        }

        #region Manejadores de Eventos de Negocio

        /// <summary>
        /// Procesa la compra de un paquete de créditos prepago (Modelo Foraes)
        /// </summary>
        private async Task HandleOrderCreatedAsync(NpgsqlConnection connection, LemonSqueezyPayload payload, Guid userId)
        {
            var attributes = payload.Data.Attributes;
            string orderId = payload.Data.Id;

            // Evitar procesamiento duplicado (Idempotencia)
            var txExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM transactions WHERE external_reference_id = @OrderId AND gateway = 'LemonSqueezy')",
                new { OrderId = orderId });

            if (txExists)
            {
                _logger.LogInformation("La orden {OrderId} ya fue procesada anteriormente. Saltando paso.", orderId);
                return;
            }

            // Identificar qué VariantId se compró (puede estar en attributes o en first_order_item)
            int variantId = attributes.VariantId != 0 
                ? attributes.VariantId 
                : (attributes.FirstOrderItem?.VariantId ?? 0);

            if (!CreditsVariantToPackageMap.TryGetValue(variantId, out var packageName))
            {
                _logger.LogWarning("Se recibió la orden {OrderId} con un VariantId ({VariantId}) no registrado para créditos.", orderId, variantId);
                return;
            }

            // Buscar la ID física del paquete en la base de datos
            var creditPackage = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, price_usd FROM credit_packages WHERE name = @Name AND active = true",
                new { Name = packageName });

            if (creditPackage == null)
            {
                _logger.LogError("El paquete de créditos '{PackageName}' no existe en la base de datos.", packageName);
                return;
            }

            Guid creditPackageId = creditPackage.id;
            decimal pricePaid = attributes.Total / 100m; // Convertir centavos de Lemon Squeezy a Decimal

            // Registrar la transacción de cobro aprobado
            // Nota: Al insertar en estado 'Completed', nuestro TRIGGER en PostgreSQL 'process_approved_credit_recharge'
            // se disparará automáticamente. Este trigger leerá los créditos base y de bono del paquete, 
            // los sumará al balance del usuario en la tabla 'users' y registrará la auditoría contable.
            var insertTxQuery = @"
                INSERT INTO transactions (id, user_id, amount, currency, gateway, status, external_reference_id, credit_package_id, created_at, updated_at)
                VALUES (@Id, @UserId, @Amount, @Currency, 'LemonSqueezy', 'Completed', @ExternalRef, @PackageId, NOW(), NOW());";

            await connection.ExecuteAsync(insertTxQuery, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = pricePaid,
                Currency = attributes.Currency,
                ExternalRef = orderId,
                PackageId = creditPackageId
            });

            _logger.LogInformation("Orden de créditos {OrderId} procesada exitosamente. Se asignó el paquete {PackageName} al usuario {UserId}.", orderId, packageName, userId);
        }

        /// <summary>
        /// Procesa la creación o renovación de suscripciones mensuales/semestrales (Modelo VectorSport)
        /// </summary>
        private async Task HandleSubscriptionUpdatedAsync(NpgsqlConnection connection, LemonSqueezyPayload payload, Guid userId)
        {
            var attributes = payload.Data.Attributes;
            string externalSubId = payload.Data.Id;
            int variantId = attributes.VariantId;

            if (!SubscriptionVariantToTierMap.TryGetValue(variantId, out var tierName))
            {
                _logger.LogWarning("VariantId ({VariantId}) de suscripción no mapeado.", variantId);
                return;
            }

            // Definir límites de descargas diarias basadas estrictamente en la membresía (Reglas VectorSport)
            int dailyDownloadLimit = tierName switch
            {
                "Basic" => 15,       // Membresía Básica -> 15 descargas diarias
                "VIP" => 25,         // Membresía VIP -> 25 descargas diarias
                "Semestral" => 30,   // Membresía Semestral -> 30 descargas diarias
                _ => 10              // Límite por defecto de seguridad
            };

            // Las fechas de vigencia de la suscripción proporcionadas por Lemon Squeezy
            DateTime startsAt = attributes.CreatedAt;
            DateTime expiresAt = attributes.RenewsAt ?? DateTime.UtcNow.AddMonths(1); // Expiración o renovación próxima

            // Buscar si ya existe la suscripción de este usuario para actualizarla o crearla
            var existingSub = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id FROM subscriptions WHERE external_sub_id = @SubId OR (user_id = @UserId AND tier = @Tier::subscription_tier)",
                new { SubId = externalSubId, UserId = userId, Tier = tierName });

            Guid subscriptionId;

            if (existingSub != null)
            {
                subscriptionId = existingSub.id;
                // Actualizar suscripción existente
                var updateQuery = @"
                    UPDATE subscriptions 
                    SET status = 'Active', 
                        daily_download_limit = @Limit,
                        starts_at = @StartsAt,
                        expires_at = @ExpiresAt,
                        updated_at = NOW()
                    WHERE id = @SubId;";

                await connection.ExecuteAsync(updateQuery, new
                {
                    SubId = subscriptionId,
                    Limit = dailyDownloadLimit,
                    StartsAt = startsAt,
                    ExpiresAt = expiresAt
                });
                _logger.LogInformation("Suscripción {SubId} ({Tier}) actualizada para el usuario {UserId}.", externalSubId, tierName, userId);
            }
            else
            {
                subscriptionId = Guid.NewGuid();
                // Registrar nueva suscripción activa
                var insertQuery = @"
                    INSERT INTO subscriptions (id, user_id, tier, status, daily_download_limit, starts_at, expires_at, external_sub_id, created_at)
                    VALUES (@Id, @UserId, @Tier::subscription_tier, 'Active', @Limit, @StartsAt, @ExpiresAt, @SubId, NOW());";

                await connection.ExecuteAsync(insertQuery, new
                {
                    Id = subscriptionId,
                    UserId = userId,
                    Tier = tierName,
                    Limit = dailyDownloadLimit,
                    StartsAt = startsAt,
                    ExpiresAt = expiresAt,
                    SubId = externalSubId
                });
                _logger.LogInformation("Nueva suscripción {SubId} ({Tier}) creada exitosamente para el usuario {UserId}.", externalSubId, tierName, userId);
            }

            // Registrar la transacción de cobro de suscripción recurrente para historial contable
            decimal pricePaid = attributes.Total / 100m;
            var insertTxQuery = @"
                INSERT INTO transactions (id, user_id, amount, currency, gateway, status, external_reference_id, subscription_id, created_at, updated_at)
                VALUES (@Id, @UserId, @Amount, @Currency, 'LemonSqueezy', 'Completed', @ExternalRef, @SubId, NOW(), NOW());";

            await connection.ExecuteAsync(insertTxQuery, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = pricePaid,
                Currency = attributes.Currency,
                ExternalRef = externalSubId,
                SubId = subscriptionId
            });
        }

        /// <summary>
        /// Procesa la cancelación o expiración de suscripciones por falta de pago o acción manual del usuario
        /// </summary>
        private async Task HandleSubscriptionCancelledAsync(NpgsqlConnection connection, LemonSqueezyPayload payload, Guid userId)
        {
            string externalSubId = payload.Data.Id;
            var attributes = payload.Data.Attributes;

            // Buscamos si la suscripción de Lemon Squeezy existe en nuestro PostgreSQL
            var sub = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, tier FROM subscriptions WHERE external_sub_id = @SubId",
                new { SubId = externalSubId });

            if (sub == null)
            {
                _logger.LogWarning("Se intentó cancelar la suscripción externa {SubId} pero no existe en el sistema local.", externalSubId);
                return;
            }

            // Si la suscripción fue 'cancelada' por el usuario pero aún le queda vigencia prepagada (ends_at futuro), 
            // se le mantiene el estatus de Active hasta el día de corte. 
            // Si el estado es 'expired' o la fecha de expiración ya pasó, pasa directamente a 'Expired'.
            string targetStatus = "Expired";
            DateTime expiresAt = attributes.EndsAt ?? DateTime.UtcNow;

            if (expiresAt > DateTime.UtcNow && payload.Meta.EventName == "subscription_cancelled")
            {
                // El usuario canceló la autorenovación pero su plan sigue vigente hasta el fin de ciclo
                targetStatus = "Canceled"; 
                _logger.LogInformation("La suscripción {SubId} fue cancelada. Expirará formalmente el {EndsAt}.", externalSubId, expiresAt);
            }
            else
            {
                _logger.LogInformation("La suscripción {SubId} ha expirado o se ha revocado inmediatamente.", externalSubId);
            }

            var updateQuery = @"
                UPDATE subscriptions 
                SET status = @Status::subscription_status,
                    expires_at = @ExpiresAt,
                    daily_download_limit = CASE WHEN @Status = 'Expired' THEN 0 ELSE daily_download_limit END,
                    updated_at = NOW()
                WHERE id = @Id;";

            await connection.ExecuteAsync(updateQuery, new
            {
                Id = (Guid)sub.id,
                Status = targetStatus,
                ExpiresAt = expiresAt
            });
        }

        #endregion

        #region Criptografía de Verificación

        /// <summary>
        /// Realiza la verificación de firmas criptográficas HMAC-SHA256 para webhooks.
        /// </summary>
        private static bool VerifyHmacSignature(string payload, string expectedSignature, string secret)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

            using var hmac = new HMACSHA256(secretBytes);
            byte[] hashBytes = hmac.ComputeHash(payloadBytes);

            // Convertir el hash generado a hexadecimal para emparejar con el formato enviado por Lemon Squeezy
            var computedSignature = Convert.ToHexString(hashBytes).ToLower();

            // Utilizar comparación en tiempo constante para mitigar ataques de temporización de canal lateral (Timing Attacks)
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedSignature.ToLower());
            byte[] computedBytes = Encoding.UTF8.GetBytes(computedSignature);

            return CryptographicOperations.FixedTimeEquals(expectedBytes, computedBytes);
        }

        #endregion
    }
}
