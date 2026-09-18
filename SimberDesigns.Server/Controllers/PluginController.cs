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
    PluginInstallerStorage installers,
    IPluginPeriodKeyService periodKeys) : ControllerBase
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

    [HttpGet("keys")]
    public async Task<ActionResult<IReadOnlyList<PluginPeriodKeyDto>>> MyKeys(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var keys = await db.PluginPeriodKeys
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Take(50)
            .Select(k => new PluginPeriodKeyDto(
                k.Id, k.Code, k.Edition, k.Days, k.Status, k.Source, k.Note, k.CreatedAt, k.RedeemedAt))
            .ToListAsync(cancellationToken);
        return Ok(keys);
    }

    /// <summary>
    /// Canjea serial SMK-… (pago MP o admin). Si mandas hardwareId, también ata la PC y firma el token.
    /// </summary>
    [HttpPost("redeem")]
    public async Task<ActionResult<object>> Redeem(PluginRedeemRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var (license, key) = await periodKeys.RedeemAsync(userId.Value, request.Code, cancellationToken);
            var hwid = request.HardwareId?.Trim();
            if (!string.IsNullOrWhiteSpace(hwid))
            {
                var edition = LicenseProgram.Normalizar(request.Edition);
                if (string.IsNullOrWhiteSpace(edition))
                {
                    edition = key.Edition;
                }

                var now = DateTime.UtcNow;
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

            return Ok(new
            {
                message = $"Canjeado: +{key.Days} días de {key.Edition}.",
                key = new PluginPeriodKeyDto(
                    key.Id, key.Code, key.Edition, key.Days, key.Status, key.Source, key.Note, key.CreatedAt, key.RedeemedAt),
                license = ToDto(license, DateTime.UtcNow)
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// El .exe manda su HWID (y opcionalmente Edition). Si el periodo está canjeado/activo, se ata a esa PC
    /// y se firma el token. 402 = sin periodo · 409 = otra PC.
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
            return StatusCode(StatusCodes.Status402PaymentRequired, "No tienes periodo activo. Canjea tu clave o renueva en la web.");
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
    public async Task<IActionResult> DownloadEdition(string edition, CancellationToken cancellationToken)
    {
        if (!PluginInstallerStorage.TryParseEdition(edition, out var program))
        {
            return BadRequest("El programa debe ser Corel o Illustrator.");
        }

        var r2Url = await installers.GetDownloadUrlAsync(program, cancellationToken);
        if (!string.IsNullOrWhiteSpace(r2Url))
        {
            return Redirect(r2Url);
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
    public async Task<ActionResult<IReadOnlyList<PluginInstallerStatusDto>>> Installers(CancellationToken cancellationToken)
        => Ok(new[]
        {
            await StatusDtoAsync(LicenseProgram.Corel, cancellationToken),
            await StatusDtoAsync(LicenseProgram.Illustrator, cancellationToken)
        });

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
        return Ok(await StatusDtoAsync(program, cancellationToken));
    }

    [HttpDelete("installers/{edition}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<PluginInstallerStatusDto>> DeleteInstaller(string edition, CancellationToken cancellationToken)
    {
        if (!PluginInstallerStorage.TryParseEdition(edition, out var program))
        {
            return BadRequest("El programa debe ser Corel o Illustrator.");
        }

        await installers.DeleteAsync(program, cancellationToken);
        return Ok(await StatusDtoAsync(program, cancellationToken));
    }

    private async Task<PluginInstallerStatusDto> StatusDtoAsync(string edition, CancellationToken cancellationToken)
    {
        var info = await installers.InfoAsync(edition, cancellationToken);
        return new PluginInstallerStatusDto(
            edition,
            info is not null,
            info is null ? null : installers.DownloadName(edition),
            info?.Length ?? 0,
            info?.LastWriteUtc);
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
