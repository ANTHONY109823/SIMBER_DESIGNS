using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SimberDesigns.Licensing;                 // verificador compartido con el .exe (copia exacta)
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// Lectura de listas con IA (Claude visión) para la V2 (web). La IA es LIBRE mientras la suscripción
/// de $15/mes esté activa: no consume créditos (los créditos son solo para descargar diseños). El plugin
/// manda la FOTO + su LICENCIA FIRMADA PREMIUM + su HWID; el servidor verifica la firma con la llave
/// pública (derivada de la privada) y, si la licencia es válida y vigente (la suscripción se renueva
/// contra Postgres cada mes), llama a Claude con la clave del servidor. La clave NUNCA sale de Railway.
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
        Eres un lector experto de listas de pedidos de ropa deportiva y uniformes (polos, camisetas),
        casi siempre escritas A MANO o en tablas impresas. Devuelve SOLO un arreglo JSON (sin texto
        adicional, sin ```), UNA entrada por PERSONA/PRENDA, con esta forma exacta:
        [{"nombre":"","numero":"","talla":"","genero":"Hombre"}]

        QUÉ NOMBRE USAR (lo más importante):
        - Si una fila trae un nombre CORTO destacado (columna "NOMBRE", resaltada o de color: un apodo o
          primer nombre) ADEMÁS del nombre largo ("APELLIDOS Y NOMBRE"), usa SIEMPRE el nombre CORTO
          destacado: ese es el que va impreso en la prenda y con el que se entrega. NUNCA uses los
          apellidos completos si existe ese nombre corto.
        - Si solo hay un nombre, usa ese. Respeta nombres compuestos o con inicial ("THIAGO A.",
          "MAIA C.", "R. BALBOA", "MARTIN.J", "David y Bianca"). No unas dos personas ni partas un nombre.

        VARIAS PERSONAS POR FILA O SECCIÓN (léelas TODAS):
        - Una misma fila puede pedir VARIAS prendas: el alumno y además su MADRE (columna N.MADRE/MAMÁ) y
          su PADRE (columna N.PADRE/PAPÁ), cada quien con SU talla y SU número en sus propias columnas.
          Crea UNA entrada por cada persona que tenga un nombre real Y (talla o número).
        - Si un nombre trae "*", "-", "—", vacío o guion => esa persona NO pidió: OMÍTELA.
        - Si la hoja tiene SECCIONES o dos columnas (MAMÁS | PAPÁS, NIÑAS y luego NIÑOS, DAMAS/VARONES),
          léelas COMPLETAS y en orden: primero una sección/columna entera de arriba a abajo, luego la otra.
        - INCLUYE a personas con etiqueta especial (PROFESORA, PROFESOR, TÍA, ENTRENADOR, DELEGADO): si
          tienen talla o número, son un pedido más. No las descartes por el título.

        CADA CAMPO:
        - "nombre": el nombre corto de la prenda (ver arriba). Si no hay un nombre real, no crees la entrada.
        - "numero": el dorsal de ESA persona (de SU columna de número). Si no hay => "".
        - "talla": de la columna de talla de ESA persona. Letra (S, M, L, XL, XXL) o número (2, 4, 6, 8,
          10, 12, 14, 16). Si hay un rango, el más probable. Si no hay => "".
        - "genero": "Hombre" o "Mujer".
           • Columna de género con una sola letra: V o H => Hombre; M, D o F => Mujer. OJO: en la columna
             de TALLA las letras S/M/L son TAMAÑOS, no género; usa la columna correcta.
           • Por sección o etiqueta: MAMÁS/DAMAS/NIÑAS/MUJERES/MADRE => Mujer; PAPÁS/CABALLEROS/NIÑOS/
             VARONES/HOMBRES/PADRE => Hombre.
           • Si de plano no se indica => "Hombre".

        IGNORA (no son personas ni pedidos):
        - Encabezados de columna (N°, Nombre, Apellidos y Nombre, Número/N°, Talla, Género).
        - Títulos del diseño, club o corte (LISTA DE ALUMNOS, CORTE PRINCESA, CUELLO REDONDO, CAMISETA,
          el nombre del equipo).
        - Notas y totales (REGALO, MUESTRA, TOTAL, PEDIDO, fechas, teléfonos, precios, sumas).
        - La numeración de filas del margen (1, 2, 3, …) que solo cuenta renglones: NO es el dorsal.
        - Texto suelto, marcas de cuaderno o de agua ("Standford") y líneas sueltas.

        REGLAS FINALES:
        - NO inventes personas ni datos. Si una fila está tachada o ilegible, omítela.
        - Si dudas de un dato puntual (un número, una talla), déjalo en "" en vez de adivinar; pero NO
          omitas a la persona si su nombre se lee.
        - Devuelve únicamente el arreglo JSON.
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

        // La licencia debe ser válida y vigente. Como el token se renueva contra Postgres solo mientras
        // la suscripción de $15/mes está pagada, una licencia vigente ES una suscripción activa.
        var check = new LicenseVerifier(publicKey).Verify(licencia, hwid);
        if (check.Status != LicenseStatus.Valid)
            return StatusCode(StatusCodes.Status403Forbidden, "Tu mes no está activo en esta PC. Renueva en la web.");

        // La IA es de la versión web (V2, PREMIUM). Las licencias V1 no la usan.
        if (check.Info?.IncluyeIa != true)
            return StatusCode(StatusCodes.Status403Forbidden, "La lectura con IA es de la versión web (V2).");

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

        // 4) Llamar a Claude (Messages API) con la foto + el prompt. IA libre: sin cobro por consulta.
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

        // 5) Devolver el texto (el arreglo JSON) tal cual lo dio Claude.
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
