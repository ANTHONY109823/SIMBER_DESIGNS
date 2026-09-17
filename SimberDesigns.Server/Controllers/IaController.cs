using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// Lectura de listas con IA (Claude visión) para la V2. IA incluida con el mes activo.
/// Tope interno por PC/día (no se comunica al cliente). Clave Anthropic solo en Railway.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/ia")]
public sealed class IaController(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    IOptions<AnthropicOptions> anthropicOptions,
    NpgsqlDataSource dataSource) : ControllerBase
{
    private const string AnthropicUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Mensaje genérico: no revela el tope diario.</summary>
    private const string ErrorLecturaGenerico =
        "No se pudo completar la lectura. Intenta con otra foto más nítida o pega la lista.";

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
            return StatusCode(StatusCodes.Status403Forbidden, "Tu mes no está activo en esta PC. Renueva en la web.");

        if (check.Info?.IncluyeIa != true)
            return StatusCode(StatusCodes.Status403Forbidden, "La lectura con IA es de la versión web (V2).");

        var opt = anthropicOptions.Value;
        var cupo = await TryConsumeDailyReadAsync(hwid, Math.Max(1, opt.DailyReadsPerPc), ct);
        if (!cupo)
            return StatusCode(StatusCodes.Status502BadGateway, ErrorLecturaGenerico);

        string b64 = (request?.image_base64 ?? "").Trim();
        string mediaType = string.IsNullOrWhiteSpace(request?.media_type) ? "image/jpeg" : request!.media_type!.Trim();
        if (b64.Length == 0)
            return BadRequest("Falta la imagen.");

        string apiKey = !string.IsNullOrWhiteSpace(opt.ApiKey)
            ? opt.ApiKey
            : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "");
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "La IA no está configurada en el servidor.");

        var model = string.IsNullOrWhiteSpace(opt.Model) ? "claude-haiku-4-5" : opt.Model.Trim();
        var maxTokens = opt.MaxTokens <= 0 ? 2048 : opt.MaxTokens;

        var payload = new
        {
            model,
            max_tokens = maxTokens,
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
        catch
        {
            return StatusCode(StatusCodes.Status502BadGateway, ErrorLecturaGenerico);
        }

        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return StatusCode(StatusCodes.Status502BadGateway, ErrorLecturaGenerico);

        string texto = ExtraerTexto(body);
        return Content(texto, "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Reserva 1 lectura del cupo diario (día Lima). Si ya está al tope, no incrementa y devuelve false.
    /// El cliente solo ve un fallo genérico de lectura.
    /// </summary>
    private async Task<bool> TryConsumeDailyReadAsync(string hwid, int dailyLimit, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // INSERT … ON CONFLICT con WHERE: si ya llegó al tope, no actualiza y RETURNING queda vacío.
        const string sql = """
            INSERT INTO ia_lectura_diaria (hwid, dia, lecturas)
            VALUES (
                @hwid,
                (CURRENT_TIMESTAMP AT TIME ZONE 'America/Lima')::date,
                1
            )
            ON CONFLICT (hwid, dia) DO UPDATE
            SET lecturas = ia_lectura_diaria.lecturas + 1
            WHERE ia_lectura_diaria.lecturas < @limit
            RETURNING lecturas;
            """;

        var row = await conn.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(sql, new { hwid, limit = dailyLimit }, cancellationToken: ct));
        return row is not null;
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
