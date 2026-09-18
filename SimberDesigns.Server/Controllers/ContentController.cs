using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// CMS: textos en Postgres. Imágenes en R2 (producción) o BYTEA (modo falso / legado).
/// </summary>
[ApiController]
[Route("api/content")]
public sealed class ContentController(AppDbContext db, ICloudflareR2Service r2) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<Dictionary<string, string>>> All(CancellationToken ct)
    {
        var dict = await db.SiteContents.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        // Marcamos qué IMÁGENES (assets) existen realmente, para que la web sepa cuáles mostrar SIN
        // consultarlas una por una: el endpoint asset/{key} redirige a R2 (otro dominio) y un fetch de
        // verificación falla por CORS. Con esto la home lee la lista directo del contenido.
        var assetKeys = await db.SiteAssets.AsNoTracking()
            .Where(a => (a.R2Key != null && a.R2Key != "") || a.Data != null)
            .Select(a => a.Key)
            .ToListAsync(ct);
        foreach (var k in assetKeys)
            dict["asset." + k] = "1";

        return Ok(dict);
    }

    [HttpPut]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Save([FromBody] Dictionary<string, string> items, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, value) in items)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var row = await db.SiteContents.FirstOrDefaultAsync(c => c.Key == key, ct);
            if (row is null)
            {
                db.SiteContents.Add(new SiteContent { Key = key.Trim(), Value = value ?? "", UpdatedAt = now });
            }
            else
            {
                row.Value = value ?? "";
                row.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{key}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var row = await db.SiteContents.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row is not null) { db.SiteContents.Remove(row); await db.SaveChangesAsync(ct); }
        return NoContent();
    }

    [HttpGet("assets")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<Dictionary<string, string>>> ListAssets(CancellationToken ct)
    {
        var rows = await db.SiteAssets.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in rows)
        {
            map[a.Key] = await ResolvePreviewUrlAsync(a, ct);
        }

        return Ok(map);
    }

    [HttpGet("asset/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> Asset(string key, CancellationToken ct)
    {
        var a = await db.SiteAssets.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (a is null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=60";

        if (r2.IsEnabled && !string.IsNullOrWhiteSpace(a.R2Key))
        {
            var pub = r2.TryBuildPublicUrl(a.R2Key);
            if (!string.IsNullOrWhiteSpace(pub))
            {
                return Redirect(pub);
            }

            var signed = await r2.GetPresignedDownloadUrlAsync(a.R2Key, ct, lifetime: r2.PreviewUrlLifetime);
            return Redirect(signed);
        }

        if (a.Data is { Length: > 0 })
        {
            return File(a.Data, string.IsNullOrWhiteSpace(a.ContentType) ? "image/jpeg" : a.ContentType);
        }

        return NotFound();
    }

    [HttpPost("asset/{key}")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadAsset(string key, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Archivo vacío.");
        if (file.Length > 15_000_000) return BadRequest("Máximo 15 MB por imagen.");

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
        var now = DateTime.UtcNow;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif"))
        {
            ext = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : contentType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
                : ".jpg";
        }

        var safeKey = key.Trim();
        var r2Key = $"cms/{safeKey}{ext}";
        byte[] bytes = Array.Empty<byte>();

        if (r2.IsEnabled)
        {
            await using var stream = file.OpenReadStream();
            await r2.UploadAsync(r2Key, stream, contentType, ct);
        }
        else
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
            r2Key = "";
        }

        var row = await db.SiteAssets.FirstOrDefaultAsync(x => x.Key == safeKey, ct);
        if (row is null)
        {
            db.SiteAssets.Add(new SiteAsset
            {
                Key = safeKey,
                ContentType = contentType,
                Data = bytes,
                R2Key = r2Key,
                UpdatedAt = now
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(row.R2Key) && row.R2Key != r2Key && r2.IsEnabled)
            {
                await r2.DeleteAsync(row.R2Key, ct);
            }

            row.ContentType = contentType;
            row.Data = bytes;
            row.R2Key = r2Key;
            row.UpdatedAt = now;
        }

        var bustKey = "cms.bust";
        var bustVal = now.Ticks.ToString();
        var bustRow = await db.SiteContents.FirstOrDefaultAsync(c => c.Key == bustKey, ct);
        if (bustRow is null)
            db.SiteContents.Add(new SiteContent { Key = bustKey, Value = bustVal, UpdatedAt = now });
        else
        {
            bustRow.Value = bustVal;
            bustRow.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        var preview = await ResolvePreviewUrlAsync(
            new SiteAsset { Key = safeKey, ContentType = contentType, Data = bytes, R2Key = r2Key },
            ct);
        return Ok(new { url = $"/api/content/asset/{safeKey}", bust = bustVal, previewUrl = preview });
    }

    [HttpDelete("asset/{key}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteAsset(string key, CancellationToken ct)
    {
        var row = await db.SiteAssets.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is not null)
        {
            if (!string.IsNullOrWhiteSpace(row.R2Key))
            {
                await r2.DeleteAsync(row.R2Key, ct);
            }

            db.SiteAssets.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    private async Task<string> ResolvePreviewUrlAsync(SiteAsset a, CancellationToken ct)
    {
        if (r2.IsEnabled && !string.IsNullOrWhiteSpace(a.R2Key))
        {
            var pub = r2.TryBuildPublicUrl(a.R2Key);
            if (!string.IsNullOrWhiteSpace(pub))
            {
                return pub;
            }

            return await r2.GetPresignedDownloadUrlAsync(a.R2Key, ct, lifetime: r2.PreviewUrlLifetime);
        }

        return $"/api/content/asset/{Uri.EscapeDataString(a.Key)}";
    }
}
