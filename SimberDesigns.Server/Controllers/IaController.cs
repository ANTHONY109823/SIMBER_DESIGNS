using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimberDesigns.Licensing;                 // verificador compartido con el .exe (copia exacta)
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// Lectura de listas con IA (Claude visión) para la V2 (web, con créditos). El plugin NO tiene la clave
/// de Anthropic: manda la FOTO + su LICENCIA FIRMADA (V2/PREMIUM) + su HWID. El servidor verifica la firma
/// con la llave pública (derivada de la privada), ubica al usuario por el HWID de su licencia, DESCUENTA
/// créditos de su saldo y recién entonces llama a Claude con la clave del servidor. Así la clave nunca sale
/// de Railway y cada lectura se cobra del mismo pool de créditos que el resto de la web.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/ia")]
public sealed class IaController(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    AppDbContext db,
    IOptions<AnthropicOptions> anthropicOptions) : ControllerBase
{
    private const string AnthropicUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private const string Prompt = """
        Eres un lector de listas de pedidos de ropa deportiva, muchas veces escritas a mano.
        Mira la foto y devuelve SOLO un arreglo JSON (sin texto antes ni después, sin ```),
        una entrada por JUGADOR, con esta forma exacta:
        [{"nombre":"","numero":"","talla":"","genero":"Hombre"}]

        Reglas:
        - "genero" es "Hombre" o "Mujer". Si hay secciones tipo MAMÁS/DAMAS/NIÑAS => Mujer;
          PAPÁS/CABALLEROS/NIÑOS/VARONES => Hombre; si no se sabe, "Hombre".
        - "numero" es el dorsal (puede faltar => "").
        - "talla" puede ser letra (S, M, L, XL...) o número (6, 8, 10, 12...). Si falta => "".
        - Respeta nombres compuestos completos (por ejemplo "R. BALBOA", "PAPA DE YUS", "MISS LAURA").
        - IGNORA: encabezados (Nombre/Número/Talla), títulos de diseño (CUELLO REDONDO, CAMISETA...),
          notas (REGALO, muestra, short) y los números de fila del margen (1,2,3...).
        - No inventes jugadores. Si una fila está tachada o ilegible, omítela.
        Devuelve únicamente el JSON.
        """;

    public sealed record LeerListaRequest(string? image_base64, string? media_type);

    [HttpPost("leer-lista")]
    public async Task<IActionResult> LeerLista([FromBody] LeerListaRequest request, CancellationToken ct)
    {
        // 1) Autenticación POR LICENCIA (el plugin manda su token firmado + HWID).
        string licencia = (Request.Headers["X-Simber-License"].ToString() ?? "").Trim();
        string hwid = (Request.Headers["X-Simber-Hwid"].ToString() ?? "").Trim();
        if (licencia.Length == 0 || hwid.Length == 0)
            return Unauthorized("Falta la licencia o el identificador de la PC.");

        string? privateKey = configuration["Licensing:PrivateKey"]
                             ?? Environment.GetEnvironmentVariable("SIMBER_LICENSE_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(privateKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "El servidor de licencias no está configurado.");

        string publicKey;
        try
        {
            using var ecdsa = LicenseKeyPair.LoadPrivate(privateKey);
            publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Configuración de licencias inválida.");
        }

        var check = new LicenseVerifier(publicKey).Verify(licencia, hwid);
        if (check.Status != LicenseStatus.Valid)
            return StatusCode(StatusCodes.Status403Forbidden, "Licencia no válida para esta PC o vencida.");

        // La IA es de la versión web (V2, PREMIUM). Las licencias V1 no la usan.
        if (check.Info?.IncluyeIa != true)
            return StatusCode(StatusCodes.Status403Forbidden, "La lectura con IA es de la versión web (V2).");

        // 2) Ubicar al usuario por el HWID de su licencia del plugin y comprobar su saldo.
        var lic = await db.PluginLicenses
            .Where(l => l.HardwareId == hwid)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        if (lic is null)
            return StatusCode(StatusCodes.Status403Forbidden, "No encontramos tu cuenta para esta PC.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == lic.UserId, ct);
        if (user is null)
            return StatusCode(StatusCodes.Status403Forbidden, "No encontramos tu cuenta.");

        decimal costo = anthropicOptions.Value.CreditsPerRead <= 0 ? 1m : anthropicOptions.Value.CreditsPerRead;
        if (user.CreditsBalance < costo)
            return StatusCode(StatusCodes.Status402PaymentRequired,
                "No tienes créditos suficientes para leer con IA. Recarga en la web para continuar.");

        // 3) Validar la imagen.
        string b64 = (request?.image_base64 ?? "").Trim();
        string mediaType = string.IsNullOrWhiteSpace(request?.media_type) ? "image/jpeg" : request!.media_type!.Trim();
        if (b64.Length == 0)
            return BadRequest("Falta la imagen.");

        // 4) Clave de Anthropic: SOLO del servidor (env de Railway). Nunca sale de aquí.
        var opt = anthropicOptions.Value;
        string apiKey = !string.IsNullOrWhiteSpace(opt.ApiKey)
            ? opt.ApiKey
            : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "");
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "La IA no está configurada en el servidor.");

        // 5) Llamar a Claude (Messages API) con la foto + el prompt.
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(opt.Model) ? "claude-sonnet-5" : opt.Model,
            max_tokens = opt.MaxTokens <= 0 ? 4096 : opt.MaxTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = mediaType, data = b64 } },
                        new { type = "text", text = Prompt }
                    }
                }
            }
        };

        using var http = httpFactory.CreateClient("anthropic");
        http.Timeout = TimeSpan.FromSeconds(90);
        using var msg = new HttpRequestMessage(HttpMethod.Post, AnthropicUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("x-api-key", apiKey);
        msg.Headers.Add("anthropic-version", AnthropicVersion);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception ex) { return StatusCode(StatusCodes.Status502BadGateway, "No se pudo contactar a la IA: " + ex.Message); }

        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return StatusCode(StatusCodes.Status502BadGateway, $"La IA respondió {(int)resp.StatusCode}.");

        // 6) Cobro: descontar créditos del mismo pool y registrar la transacción. Solo tras éxito.
        user.CreditsBalance -= costo;
        user.UpdatedAt = DateTime.UtcNow;
        db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreditsChanged = -costo,
            TxType = CreditTxTypes.AiRead,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        Response.Headers["X-Simber-Credits"] = user.CreditsBalance.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 7) Devolver el texto (el arreglo JSON) tal cual lo dio Claude.
        string texto = ExtraerTexto(body);
        return Content(texto, "text/plain; charset=utf-8");
    }

    private static string ExtraerTexto(string anthropicJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(anthropicJson);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return "";
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}
