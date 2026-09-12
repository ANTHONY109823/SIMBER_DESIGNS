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

public interface IMercadoPagoService
{
    bool UseFakeCheckout { get; }
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
    public bool UseFakeCheckout => options.Value.UseFakeCheckout
        || string.IsNullOrWhiteSpace(options.Value.AccessToken)
        || options.Value.AccessToken.StartsWith("dev-", StringComparison.OrdinalIgnoreCase);

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
