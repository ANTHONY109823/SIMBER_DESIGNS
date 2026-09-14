using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SimberDesigns.Licensing;                 // verificador compartido con el .exe (copia exacta)
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// Lectura de listas con IA (Claude visión) para el plugin V1 (offline, sin login web).
/// El .exe NO tiene la clave de Anthropic: manda la FOTO + su LICENCIA FIRMADA y su HWID; aquí se
/// verifica la firma con la llave pública (derivada de la privada del servidor) y, solo si la licencia
/// es válida para esa PC, se llama a Claude con la clave del servidor. Así la clave nunca sale de Railway
/// y solo los clientes con licencia vigente gastan crédito.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/ia")]
public sealed class IaController(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
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
        // 1) Autenticación POR LICENCIA (no hay login web en V1).
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

        // 2) Validar la imagen.
        string b64 = (request?.image_base64 ?? "").Trim();
        string mediaType = string.IsNullOrWhiteSpace(request?.media_type) ? "image/jpeg" : request!.media_type!.Trim();
        if (b64.Length == 0)
            return BadRequest("Falta la imagen.");

        // 3) Clave de Anthropic: SOLO del servidor (env de Railway). Nunca sale de aquí.
        var opt = anthropicOptions.Value;
        string apiKey = !string.IsNullOrWhiteSpace(opt.ApiKey)
            ? opt.ApiKey
            : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "");
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "La IA no está configurada en el servidor.");

        // 4) Llamar a Claude (Messages API) con la foto + el prompt.
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

        // 5) Extraer el texto (el arreglo JSON) de la respuesta de Claude y devolverlo tal cual.
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
