using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Licensing;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/plugin")]
public sealed class PluginController(
    AppDbContext db,
    IConfiguration configuration,
    PluginInstallerStorage installers) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<PluginLicenseDto>>> Mine(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var licenses = await db.PluginLicenses
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .ToListAsync(cancellationToken);

        return Ok(licenses.Select(l => ToDto(l, now)).ToList());
    }

    /// <summary>
    /// El .exe manda su HWID (y opcionalmente Edition). Si el mes está pago, se ata a esa PC
    /// y se firma el token. 402 = sin mes · 409 = otra PC.
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

        var edition = LicenseProgram.Normalizar(request.Edition);
        var license = await FindLicenseAsync(userId.Value, hwid, edition, cancellationToken);
        var now = DateTime.UtcNow;
        if (license is null || license.ExpiresAt <= now)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, "No tienes el mes pagado. Renueva en la web.");
        }

        if (string.IsNullOrWhiteSpace(license.HardwareId))
        {
            license.HardwareId = hwid;
            if (string.IsNullOrWhiteSpace(license.Edition) && !string.IsNullOrWhiteSpace(edition))
            {
                license.Edition = edition;
            }

            license.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(license.HardwareId.Trim(), hwid, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("Esta cuenta ya está activada en otra PC.");
        }

        return Ok(EmitirToken(license, hwid));
    }

    [HttpGet("token")]
    public async Task<ActionResult<PluginTokenDto>> Token([FromQuery] string? edition, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var wanted = LicenseProgram.Normalizar(edition);
        var query = db.PluginLicenses
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc);
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            query = query.Where(l => l.Edition == wanted || l.Edition == "");
        }

        var license = await query
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

    /// <summary>Al abrir el programa: verifica por internet si el mes sigue vigente en esta PC.</summary>
    [HttpPost("revalidate")]
    [AllowAnonymous]
    public async Task<ActionResult<PluginTokenDto>> Revalidate(CancellationToken cancellationToken)
    {
        string licenseText = (Request.Headers["X-Simber-License"].ToString() ?? "").Trim();
        string hwid = (Request.Headers["X-Simber-Hwid"].ToString() ?? "").Trim();
        if (licenseText.Length == 0 || hwid.Length == 0)
        {
            return Unauthorized("Falta la licencia o el identificador de la PC.");
        }

        string publicKey;
        try
        {
            using var ecdsa = LicenseKeyPair.LoadPrivate(PrivateKeyBase64());
            publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Configuración de licencias inválida.");
        }

        var check = new LicenseVerifier(publicKey).Verify(licenseText, hwid);
        if (check.Status is not (LicenseStatus.Valid or LicenseStatus.Expired))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Licencia inválida para esta PC.");
        }

        var edition = LicenseProgram.Normalizar(check.Info?.Edition);
        var query = db.PluginLicenses.Where(l => l.HardwareId == hwid && l.Plan == PluginPlans.Month1Pc);
        if (!string.IsNullOrWhiteSpace(edition))
        {
            query = query.Where(l => l.Edition == edition || l.Edition == "");
        }

        var license = await query
            .OrderByDescending(l => l.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (license is null || license.ExpiresAt <= DateTime.UtcNow)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, "Tu mes no está activo. Renueva en la web.");
        }

        return Ok(EmitirToken(license, hwid));
    }

    [HttpGet("download")]
    [AllowAnonymous]
    public IActionResult Download()
        => NotFound("Elige CorelDRAW o Illustrator: /api/plugin/download/corel o /api/plugin/download/illustrator");

    [HttpGet("download/{edition}")]
    [AllowAnonymous]
    public IActionResult DownloadEdition(string edition)
    {
        if (!PluginInstallerStorage.TryParseEdition(edition, out var program))
        {
            return BadRequest("El programa debe ser Corel o Illustrator.");
        }

        if (installers.Exists(program))
        {
            return PhysicalFile(installers.FilePath(program), "application/octet-stream", installers.DownloadName(program));
        }

        var url = program == LicenseProgram.Illustrator
            ? configuration["Plugin:DownloadUrlIllustrator"]
            : configuration["Plugin:DownloadUrlCorel"];
        if (string.IsNullOrWhiteSpace(url))
        {
            return NotFound("El administrador aún no subió este ejecutable.");
        }

        return Redirect(url);
    }

    [HttpGet("installers")]
    [Authorize(Roles = Roles.Admin)]
    public ActionResult<IReadOnlyList<PluginInstallerStatusDto>> Installers()
        => Ok(new[] { StatusDto(LicenseProgram.Corel), StatusDto(LicenseProgram.Illustrator) });

    [HttpPost("installers/{edition}")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(110_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 110_000_000)]
    public async Task<ActionResult<PluginInstallerStatusDto>> UploadInstaller(string edition, IFormFile? file, CancellationToken cancellationToken)
    {
        if (!PluginInstallerStorage.TryParseEdition(edition, out var program))
        {
            return BadRequest("El programa debe ser Corel o Illustrator.");
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Sube el .exe.");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".exe" or ".zip"))
        {
            return BadRequest("Solo se acepta .exe (o un ZIP del instalador).");
        }

        await installers.SaveAsync(program, file, cancellationToken);
        return Ok(StatusDto(program));
    }

    private PluginInstallerStatusDto StatusDto(string edition)
    {
        var info = installers.Info(edition);
        return new PluginInstallerStatusDto(
            edition,
            info is not null,
            info is null ? null : installers.DownloadName(edition),
            info?.Length ?? 0,
            info?.LastWriteTimeUtc);
    }

    private async Task<PluginLicense?> FindLicenseAsync(Guid userId, string hwid, string edition, CancellationToken cancellationToken)
    {
        var licenses = await db.PluginLicenses
            .Where(l => l.UserId == userId && l.Plan == PluginPlans.Month1Pc)
            .OrderByDescending(l => l.ExpiresAt)
            .ToListAsync(cancellationToken);

        var samePc = licenses.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l.HardwareId)
            && string.Equals(l.HardwareId.Trim(), hwid, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(edition) || string.IsNullOrWhiteSpace(l.Edition) || string.Equals(l.Edition, edition, StringComparison.OrdinalIgnoreCase)));
        if (samePc is not null)
        {
            return samePc;
        }

        IEnumerable<PluginLicense> pool = licenses;
        if (!string.IsNullOrWhiteSpace(edition))
        {
            var match = licenses.Where(l => string.Equals(l.Edition, edition, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count > 0)
            {
                pool = match;
            }
        }

        return pool.FirstOrDefault(l => string.IsNullOrWhiteSpace(l.HardwareId))
               ?? pool.FirstOrDefault();
    }

    private static PluginLicenseDto ToDto(PluginLicense license, DateTime now)
    {
        var active = license.Status == PluginLicenseStatuses.Active && license.ExpiresAt > now;
        return new PluginLicenseDto(
            license.Plan,
            active ? PluginLicenseStatuses.Active : PluginLicenseStatuses.Expired,
            license.ActivationCode,
            license.ExpiresAt,
            active,
            license.HardwareId,
            license.Edition);
    }

    private PluginTokenDto EmitirToken(PluginLicense license, string hardwareId)
    {
        var signer = new LicenseSigner(PrivateKeyBase64());
        var edition = string.IsNullOrWhiteSpace(license.Edition) ? LicenseProgram.Legacy : license.Edition;
        string token = signer.Issue(hardwareId, LicensePlan.Mensual, license.ExpiresAt, customer: "", incluyeIa: true, edition: edition);
        return new PluginTokenDto(token, license.ExpiresAt, license.Plan, license.Edition);
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
