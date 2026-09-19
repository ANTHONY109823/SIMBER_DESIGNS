using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

public sealed record MercadoPagoPreferenceResult(string PreferenceId, string CheckoutUrl);

public sealed record MercadoPagoPaymentResult(
    string PaymentId,
    string Status,
    string? ExternalReference,
    decimal TransactionAmount);

public sealed record MercadoPagoCardResult(
    string? PaymentId,
    string Status,
    string? StatusDetail,
    decimal TransactionAmount,
    string? Error);

public interface IMercadoPagoService
{
    bool UseFakeCheckout { get; }
    string PublicKey { get; }
    Task<MercadoPagoCardResult> CreateCardPaymentAsync(
        decimal amountPen,
        string token,
        string paymentMethodId,
        string? issuerId,
        int installments,
        string payerEmail,
        string description,
        string externalReference,
        string notificationUrl,
        CancellationToken cancellationToken);
    Task<MercadoPagoPreferenceResult> CreatePreferenceAsync(
        string title,
        decimal amountPen,
        string payerEmail,
        string externalReference,
        string notificationUrl,
        string successUrl,
        string failureUrl,
        string pendingUrl,
        CancellationToken cancellationToken);
    Task<MercadoPagoPaymentResult?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken);
    bool IsValidWebhook(string? xSignature, string? xRequestId, string dataId);
}

public sealed class MercadoPagoService(HttpClient http, IOptions<MercadoPagoOptions> options) : IMercadoPagoService
{
    /// <summary>
    /// SEGURIDAD: con un token de PRODUCCIÓN real de Mercado Pago (empieza con "APP_USR-") NUNCA se usa
    /// el checkout falso, aunque la config traiga el flag en true por descuido: así nadie puede acreditar
    /// pagos falsos en producción (ni interceptando con Burp). El modo falso queda solo para desarrollo
    /// (sin token, token "dev-", o flag explícito de pruebas con token de test).
    /// </summary>
    public bool UseFakeCheckout
    {
        get
        {
            var token = (options.Value.AccessToken ?? string.Empty).Trim();
            if (token.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
                return false;
            return options.Value.UseFakeCheckout
                || string.IsNullOrWhiteSpace(token)
                || token.StartsWith("dev-", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string PublicKey => (options.Value.PublicKey ?? string.Empty).Trim();

    /// <summary>
    /// Crea un pago con TARJETA directamente en la web (Checkout API / Bricks): el navegador tokeniza la
    /// tarjeta con la Public Key y aquí solo llega el token — los datos de la tarjeta NUNCA pasan por el
    /// servidor. El monto lo fija el servidor (no el cliente), y luego el controlador verifica que el pago
    /// aprobado tenga external_reference y monto correctos antes de acreditar.
    /// </summary>
    public async Task<MercadoPagoCardResult> CreateCardPaymentAsync(
        decimal amountPen,
        string token,
        string paymentMethodId,
        string? issuerId,
        int installments,
        string payerEmail,
        string description,
        string externalReference,
        string notificationUrl,
        CancellationToken cancellationToken)
    {
        EnsureHttpsUrl(notificationUrl, "notification_url");

        var body = new Dictionary<string, object?>
        {
            ["transaction_amount"] = amountPen,
            ["token"] = token,
            ["description"] = description,
            ["installments"] = installments <= 0 ? 1 : installments,
            ["payment_method_id"] = paymentMethodId,
            ["payer"] = new { email = payerEmail },
            ["external_reference"] = externalReference,
            ["notification_url"] = notificationUrl,
            ["statement_descriptor"] = "SIMBER"
        };
        if (!string.IsNullOrWhiteSpace(issuerId))
        {
            body["issuer_id"] = issuerId;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/v1/payments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        // Idempotencia por transacción: reintentos con la misma clave no duplican el cargo.
        request.Headers.TryAddWithoutValidation("X-Idempotency-Key", externalReference);
        request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = json;
            try
            {
                using var errDoc = JsonDocument.Parse(json);
                if (errDoc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    err = m.GetString() ?? json;
                }
            }
            catch (JsonException) { }
            return new MercadoPagoCardResult(null, "error", null, amountPen, err);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new MercadoPagoCardResult(
            root.TryGetProperty("id", out var id) ? id.ToString() : null,
            root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            root.TryGetProperty("status_detail", out var sd) ? sd.GetString() : null,
            root.TryGetProperty("transaction_amount", out var ta) ? ta.GetDecimal() : amountPen,
            null);
    }

    public async Task<MercadoPagoPreferenceResult> CreatePreferenceAsync(
        string title,
        decimal amountPen,
        string payerEmail,
        string externalReference,
        string notificationUrl,
        string successUrl,
        string failureUrl,
        string pendingUrl,
        CancellationToken cancellationToken)
    {
        EnsureHttpsUrl(successUrl, "back_urls.success");
        EnsureHttpsUrl(failureUrl, "back_urls.failure");
        EnsureHttpsUrl(pendingUrl, "back_urls.pending");
        EnsureHttpsUrl(notificationUrl, "notification_url");

        var body = new
        {
            items = new[]
            {
                new
                {
                    title,
                    quantity = 1,
                    currency_id = "PEN",
                    unit_price = amountPen
                }
            },
            payer = new { email = payerEmail },
            back_urls = new
            {
                success = successUrl,
                failure = failureUrl,
                pending = pendingUrl
            },
            auto_return = "approved",
            notification_url = notificationUrl,
            external_reference = externalReference,
            statement_descriptor = "SIMBER"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/checkout/preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Mercado Pago no pudo crear el checkout: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? "";
        var checkout = options.Value.UseSandbox
            && root.TryGetProperty("sandbox_init_point", out var sandbox)
            && sandbox.ValueKind == JsonValueKind.String
            ? sandbox.GetString()
            : root.GetProperty("init_point").GetString();

        if (string.IsNullOrWhiteSpace(checkout))
        {
            throw new InvalidOperationException("Mercado Pago no devolvió la URL de pago.");
        }

        return new MercadoPagoPreferenceResult(id, checkout);
    }

    private static void EnsureHttpsUrl(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{name} debe ser https://… (configura MercadoPago__PublicBaseUrl con la URL pública). Valor: {url}");
        }
    }

    public async Task<MercadoPagoPaymentResult?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadopago.com/v1/payments/{Uri.EscapeDataString(paymentId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new MercadoPagoPaymentResult(
            root.GetProperty("id").ToString(),
            root.GetProperty("status").GetString() ?? "",
            root.TryGetProperty("external_reference", out var ext) ? ext.GetString() : null,
            root.TryGetProperty("transaction_amount", out var amount) ? amount.GetDecimal() : 0);
    }

    public bool IsValidWebhook(string? xSignature, string? xRequestId, string dataId)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || secret.StartsWith("dev-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(xSignature) || string.IsNullOrWhiteSpace(dataId))
        {
            return false;
        }

        string? ts = null;
        string? hash = null;
        foreach (var part in xSignature.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (split.Length != 2)
            {
                continue;
            }

            if (split[0] == "ts")
            {
                ts = split[1];
            }
            else if (split[0] == "v1")
            {
                hash = split[1];
            }
        }

        if (string.IsNullOrWhiteSpace(ts) || string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var manifest = $"id:{dataId};request-id:{xRequestId};ts:{ts};";
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try
        {
            var provided = Convert.FromHexString(hash);
            return provided.Length == computed.Length
                && CryptographicOperations.FixedTimeEquals(computed, provided);
        }
        catch (FormatException)
        {
            CryptographicOperations.FixedTimeEquals(computed, computed);
            return false;
        }
    }
}
