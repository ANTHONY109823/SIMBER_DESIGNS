using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;                 // firmador compartido con el .exe (copia exacta)
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/plugin")]
public sealed class PluginController(AppDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<PluginLicenseDto>> Mine(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var license = await db.PluginLicenses
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (license is null)
        {
            return NotFound("Aún no hay licencia del plugin. Paga el mes en Recargar.");
        }

        var active = license.Status == PluginLicenseStatuses.Active && license.ExpiresAt > DateTime.UtcNow;
        return Ok(new PluginLicenseDto(
            license.Plan,
            active ? PluginLicenseStatuses.Active : PluginLicenseStatuses.Expired,
            license.ActivationCode,
            license.ExpiresAt,
            active,
            license.HardwareId));
    }

    /// <summary>
    /// "Keygen web": el plugin manda su HWID y, si el mes está pago, el servidor ATA la licencia a esa
    /// PC (una sola vez) y devuelve un TOKEN FIRMADO con la llave privada. El plugin lo valida offline.
    /// 402 = sin mes pagado · 409 = la cuenta ya está atada a otra PC.
    /// </summary>
    [HttpPost("activate")]
    public async Task<ActionResult<PluginTokenDto>> Activate(PluginActivateRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var hwid = request.HardwareId?.Trim();
        if (string.IsNullOrWhiteSpace(hwid))
        {
            return BadRequest("Falta el hardwareId de la PC.");
        }

        var license = await db.PluginLicenses
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        if (license is null || license.ExpiresAt <= now)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, "No tienes el mes pagado. Renueva en la web.");
        }

        // Atar a ESTA PC la primera vez; rechazar si ya pertenece a otra.
        if (string.IsNullOrWhiteSpace(license.HardwareId))
        {
            license.HardwareId = hwid;
            license.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(license.HardwareId.Trim(), hwid, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("Esta cuenta ya está activada en otra PC.");
        }

        return Ok(EmitirToken(license, hwid));
    }

    /// <summary>Re-emite el token si la licencia sigue activa y el HWID coincide (renovar al abrir).</summary>
    [HttpGet("token")]
    public async Task<ActionResult<PluginTokenDto>> Token(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var license = await db.PluginLicenses
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        if (license is null || license.ExpiresAt <= now)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, "No tienes el mes pagado.");
        }
        if (string.IsNullOrWhiteSpace(license.HardwareId))
        {
            return Conflict("La licencia aún no está atada a ninguna PC. Activa primero.");
        }

        return Ok(EmitirToken(license, license.HardwareId));
    }

    /// <summary>
    /// Descarga del instalador. Es GRATIS/anónima (el cobro se exige al ACTIVAR, no al descargar):
    /// "descarga, paga el mes y se activa sola". Redirige a la URL configurada del .exe (R2 / GitHub
    /// Releases). Mientras no esté configurada, avisa que estará disponible pronto.
    /// </summary>
    [HttpGet("download")]
    [AllowAnonymous]
    public IActionResult Download()
    {
        var url = configuration["Plugin:DownloadUrl"]
                  ?? Environment.GetEnvironmentVariable("SIMBER_PLUGIN_DOWNLOAD_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return NotFound("La descarga estará disponible en breve.");
        }
        return Redirect(url);
    }

    // ---- Firma del token con la llave privada (secreto de entorno, nunca en el repo) ----
    private PluginTokenDto EmitirToken(PluginLicense license, string hardwareId)
    {
        var signer = new LicenseSigner(PrivateKeyBase64());
        // V2 (web) = versión PREMIUM con IA. Vencimiento = el de la licencia en la BD.
        string token = signer.Issue(hardwareId, LicensePlan.Mensual, license.ExpiresAt, customer: "", incluyeIa: true);
        return new PluginTokenDto(token, license.ExpiresAt, license.Plan);
    }

    private string PrivateKeyBase64()
    {
        var key = configuration["Licensing:PrivateKey"]
                  ?? Environment.GetEnvironmentVariable("SIMBER_LICENSE_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Falta la llave privada de licencias. Configura SIMBER_LICENSE_PRIVATE_KEY (o Licensing:PrivateKey).");
        }
        return key;
    }
}
