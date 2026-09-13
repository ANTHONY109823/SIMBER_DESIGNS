using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Controllers;

/// <summary>
/// CMS: contenido editable de la web. Los textos y las imágenes viven en la BD (Postgres).
/// Público: leer textos e imágenes. Admin: guardar/eliminar textos y subir imágenes.
/// </summary>
[ApiController]
[Route("api/content")]
public sealed class ContentController(AppDbContext db) : ControllerBase
{
    // ---- Textos ----
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<Dictionary<string, string>>> All(CancellationToken ct)
    {
        var dict = await db.SiteContents.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value, ct);
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

    // ---- Imágenes (guardadas en la BD) ----
    [HttpGet("asset/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> Asset(string key, CancellationToken ct)
    {
        var a = await db.SiteAssets.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (a is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=60";
        return File(a.Data, string.IsNullOrWhiteSpace(a.ContentType) ? "image/jpeg" : a.ContentType);
    }

    [HttpPost("asset/{key}")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadAsset(string key, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Archivo vacío.");
        if (file.Length > 15_000_000) return BadRequest("Máximo 15 MB por imagen.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
        var now = DateTime.UtcNow;

        var row = await db.SiteAssets.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null)
        {
            db.SiteAssets.Add(new SiteAsset { Key = key.Trim(), ContentType = contentType, Data = bytes, UpdatedAt = now });
        }
        else
        {
            row.ContentType = contentType;
            row.Data = bytes;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { url = $"/api/content/asset/{key}" });
    }

    [HttpDelete("asset/{key}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteAsset(string key, CancellationToken ct)
    {
        var row = await db.SiteAssets.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is not null) { db.SiteAssets.Remove(row); await db.SaveChangesAsync(ct); }
        return NoContent();
    }
}
