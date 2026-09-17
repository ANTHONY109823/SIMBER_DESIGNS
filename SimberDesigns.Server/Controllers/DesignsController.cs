using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class DesignsController(
    AppDbContext db,
    IEmbeddingService embeddings,
    ICloudflareR2Service r2,
    IDownloadLimitService downloadLimits,
    LocalCatalogStorage localFiles) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<DesignDto>>> List(
        [FromQuery] string? category,
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        var query = db.Designs.AsNoTracking()
            .Where(d => d.Category == "Fútbol" || d.Category == "Vóley" || d.Category == "Jersey" || d.Category == "Voleibol");
        if (!string.IsNullOrWhiteSpace(category) && !string.Equals(category, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(d => d.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(d => d.Title.Contains(term) || (d.Description != null && d.Description.Contains(term)));
        }

        var rows = await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        var items = new List<DesignDto>(rows.Count);
        foreach (var d in rows)
        {
            items.Add(await ToDtoAsync(d, null, cancellationToken));
        }

        return Ok(items);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost("generate-upload-url")]
    public async Task<ActionResult<PresignedUrlResponse>> GetUploadUrl(
        [FromQuery] string fileName,
        [FromQuery] string contentType,
        CancellationToken cancellationToken)
    {
        if (!r2.IsEnabled)
        {
            return BadRequest("R2 no está activo. Configura CloudflareR2 en el servidor (UseFakeClient=false).");
        }

        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(contentType))
        {
            return BadRequest("El nombre de archivo y Content-Type son obligatorios.");
        }

        var safeName = Path.GetFileName(fileName);
        var fileKey = $"designs/{Guid.NewGuid():N}_{safeName}";
        var uploadUrl = await r2.GetPresignedUploadUrlAsync(fileKey, contentType, cancellationToken);
        return Ok(new PresignedUrlResponse(uploadUrl, fileKey));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost]
    [RequestSizeLimit(110_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 110_000_000)]
    public async Task<ActionResult<DesignDto>> Create(
        [FromForm] string title,
        [FromForm] string category,
        [FromForm] decimal price,
        [FromForm] string? cdrVersion,
        IFormFile? preview,
        IFormFile? photo,
        IFormFile? file,
        IFormFile? download,
        [FromForm] bool isFreeDaily = false,
        CancellationToken cancellationToken = default)
    {
        var image = preview ?? photo;
        var pack = file ?? download;
        if (string.IsNullOrWhiteSpace(title) || image is null || image.Length == 0 || pack is null || pack.Length == 0)
        {
            return BadRequest("Nombre, foto del diseño y archivo descargable son obligatorios.");
        }

        if (price < 0)
        {
            return BadRequest("El precio no puede ser negativo.");
        }

        var sport = NormalizeSport(category);
        if (sport is null)
        {
            return BadRequest("La categoría debe ser Fútbol o Vóley.");
        }

        var previewExt = Path.GetExtension(image.FileName).ToLowerInvariant();
        if (previewExt is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            return BadRequest("La foto debe ser JPG, PNG o WEBP.");
        }

        var packExt = Path.GetExtension(pack.FileName).ToLowerInvariant();
        var allowedPacks = new[] { ".cdr", ".zip", ".rar", ".7z", ".ai", ".eps" };
        if (packExt == ".pdf" || !allowedPacks.Contains(packExt))
        {
            return BadRequest("El descargable debe ser CDR (o ZIP/RAR del CDR). No se acepta PDF.");
        }

        var id = Guid.NewGuid();
        var slug = await UniqueSlugAsync(title, cancellationToken);
        var version = string.IsNullOrWhiteSpace(cdrVersion) ? "CDR" : cdrVersion.Trim();
        var r2Key = $"designs/{id:N}{packExt}";
        var previewR2Key = $"previews/{id:N}{previewExt}";
        var previewContentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType;
        var packContentType = string.IsNullOrWhiteSpace(pack.ContentType) ? "application/octet-stream" : pack.ContentType;

        string previewUrl;
        float[] embedding;

        if (r2.IsEnabled)
        {
            await using (var previewStream = image.OpenReadStream())
            {
                await r2.UploadAsync(previewR2Key, previewStream, previewContentType, cancellationToken);
            }

            await using (var packStream = pack.OpenReadStream())
            {
                await r2.UploadAsync(r2Key, packStream, packContentType, cancellationToken);
            }

            await using (var embedStream = image.OpenReadStream())
            {
                embedding = await embeddings.EmbedImageAsync(embedStream, cancellationToken);
            }

            previewUrl = r2.TryBuildPublicUrl(previewR2Key)
                         ?? $"/api/designs/{id}/preview";
        }
        else
        {
            previewUrl = await localFiles.SavePreviewAsync(id, image, cancellationToken);
            await localFiles.SaveDownloadAsync(id, pack, cancellationToken);
            // Intento R2 falso (no-op) para no romper el contrato
            await using (var previewStream = image.OpenReadStream())
            {
                await r2.UploadAsync(previewR2Key, previewStream, previewContentType, cancellationToken);
            }

            await using (var packStream = pack.OpenReadStream())
            {
                await r2.UploadAsync(r2Key, packStream, packContentType, cancellationToken);
            }

            var previewPath = Path.Combine(localFiles.PreviewRoot, Path.GetFileName(previewUrl));
            await using (var embedStream = System.IO.File.OpenRead(previewPath))
            {
                embedding = await embeddings.EmbedImageAsync(embedStream, cancellationToken);
            }

            previewR2Key = "";
        }

        var design = new Design
        {
            Id = id,
            Title = title.Trim(),
            Slug = slug,
            Description = version,
            Category = sport,
            PriceUsd = price,
            CreditsCost = price,
            R2Key = r2Key,
            PreviewR2Key = previewR2Key,
            PreviewUrl = previewUrl,
            IsFreeDaily = isFreeDaily,
            Embedding = new Vector(embedding),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Designs.Add(design);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(await ToDtoAsync(design, null, cancellationToken));
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var design = await db.Designs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (design is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(design.R2Key))
        {
            await r2.DeleteAsync(design.R2Key, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(design.PreviewR2Key))
        {
            await r2.DeleteAsync(design.PreviewR2Key, cancellationToken);
        }

        localFiles.Delete(id, design.PreviewUrl);
        db.Designs.Remove(design);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Preview público: 302 a R2 (sin pasar los bytes por Railway) o archivo local en modo falso.</summary>
    [HttpGet("{id:guid}/preview")]
    [AllowAnonymous]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        var design = await db.Designs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (design is null)
        {
            return NotFound();
        }

        if (r2.IsEnabled && !string.IsNullOrWhiteSpace(design.PreviewR2Key))
        {
            var publicUrl = r2.TryBuildPublicUrl(design.PreviewR2Key);
            if (!string.IsNullOrWhiteSpace(publicUrl))
            {
                return Redirect(publicUrl);
            }

            var signed = await r2.GetPresignedDownloadUrlAsync(
                design.PreviewR2Key,
                cancellationToken,
                lifetime: r2.PreviewUrlLifetime);
            return Redirect(signed);
        }

        if (!string.IsNullOrWhiteSpace(design.PreviewUrl)
            && design.PreviewUrl.StartsWith("/catalog-previews/", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(design.PreviewUrl);
        }

        return NotFound("Preview no disponible.");
    }

    [Authorize]
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> DownloadFile(Guid id, CancellationToken cancellationToken)
    {
        // Legacy: solo modo falso / disco local. Con R2 activo no se sirve por Railway.
        if (r2.IsEnabled)
        {
            return BadRequest("Con R2 activo usa /api/designs/{id}/download (URL firmada).");
        }

        var path = localFiles.FindDownloadPath(id);
        if (path is null)
        {
            return NotFound("El archivo descargable no está en este servidor.");
        }

        var name = Path.GetFileName(path);
        return await Task.FromResult(PhysicalFile(path, "application/octet-stream", name));
    }

    [HttpPost("visual-search")]
    [AllowAnonymous]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<IReadOnlyList<DesignDto>>> VisualSearch(
        IFormFile? imageFile,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        var file = imageFile ?? image;
        if (file is null || file.Length == 0)
        {
            return BadRequest("Se requiere una imagen válida para la búsqueda visual.");
        }

        await using var stream = file.OpenReadStream();
        var vectorValues = await embeddings.EmbedImageAsync(stream, cancellationToken);
        if (vectorValues.Length != OnnxEmbeddingService.ClipDimensions)
        {
            return StatusCode(500, "El embedding no tiene 512 dimensiones.");
        }

        var vector = new Vector(vectorValues);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        const string sql = """
            SELECT id AS Id, (1 - (embedding <=> @Embedding))::real AS Similarity
            FROM designs
            WHERE embedding IS NOT NULL
              AND category IN ('Fútbol', 'Vóley', 'Jersey', 'Voleibol')
            ORDER BY embedding <=> @Embedding
            LIMIT 8
            """;

        var hits = (await connection.QueryAsync<(Guid Id, float Similarity)>(sql, new { Embedding = vector })).AsList();
        if (hits.Count == 0)
        {
            return Ok(Array.Empty<DesignDto>());
        }

        var ids = hits.Select(h => h.Id).ToList();
        var designs = await db.Designs.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var items = new List<DesignDto>(hits.Count);
        foreach (var hit in hits)
        {
            if (!designs.TryGetValue(hit.Id, out var design))
            {
                continue;
            }

            items.Add(await ToDtoAsync(design, hit.Similarity, cancellationToken));
        }

        return Ok(items);
    }

    [Authorize]
    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> UnlockWithCredits(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var design = await db.Designs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (user is null)
        {
            return Unauthorized();
        }

        if (design is null)
        {
            return NotFound("Diseño no encontrado.");
        }

        var alreadyOwned = await db.CreditTransactions.AnyAsync(
            c => c.UserId == user.Id && c.DesignId == design.Id && c.TxType == CreditTxTypes.PurchaseDesign,
            cancellationToken);
        if (alreadyOwned)
        {
            return Ok(new { message = "Este diseño ya está desbloqueado." });
        }

        if (design.IsFreeDaily)
        {
            return Ok(new { message = "Gratis del día: no consume créditos.", creditsBalance = user.CreditsBalance });
        }

        if (user.CreditsBalance < design.CreditsCost)
        {
            return BadRequest("No tienes créditos suficientes para canjear este vector.");
        }

        user.CreditsBalance -= design.CreditsCost;
        db.CreditTransactions.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DesignId = design.Id,
            CreditsChanged = -design.CreditsCost,
            TxType = CreditTxTypes.PurchaseDesign,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Diseño desbloqueado con créditos.", creditsBalance = user.CreditsBalance });
    }

    [Authorize]
    [HttpGet("{id:guid}/download")]
    public async Task<ActionResult<DownloadResponse>> Download(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var design = await db.Designs.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (design is null)
        {
            return NotFound("Diseño no encontrado.");
        }

        if (User.IsInRole(Roles.Admin))
        {
            var adminUrl = await ResolvePackDownloadUrlAsync(design, cancellationToken);
            return Ok(new DownloadResponse(adminUrl, DateTime.UtcNow.Add(r2.UrlLifetime), 0));
        }

        var evaluation = await downloadLimits.EvaluateAsync(userId.Value, cancellationToken);
        var purchased = await db.CreditTransactions.AnyAsync(
            c => c.UserId == userId && c.DesignId == id && c.TxType == CreditTxTypes.PurchaseDesign,
            cancellationToken);
        var bought = await db.Transactions.AnyAsync(
            t => t.UserId == userId && t.DesignId == id && t.Status == TransactionStatuses.Completed,
            cancellationToken);

        var hasAccess = purchased || bought || design.IsFreeDaily;
        var remaining = 0;
        if (evaluation.Subscription is not null)
        {
            if (!evaluation.Allowed)
            {
                return BadRequest($"Has alcanzado tu límite de descarga diario de {evaluation.Limit} archivos para tu membresía.");
            }

            hasAccess = true;
            remaining = MembershipLimits.Remaining(evaluation.Used + 1, evaluation.Limit);
        }

        if (!hasAccess)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "No tienes acceso a este diseño. Adquiere una membresía o canjea créditos.");
        }

        db.UserDownloads.Add(new UserDownload
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            DesignId = id,
            DownloadedAt = DateTime.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await db.SaveChangesAsync(cancellationToken);

        var url = await ResolvePackDownloadUrlAsync(design, cancellationToken);
        return Ok(new DownloadResponse(url, DateTime.UtcNow.Add(r2.UrlLifetime), remaining));
    }

    private async Task<string> ResolvePackDownloadUrlAsync(Design design, CancellationToken cancellationToken)
    {
        var fileName = $"{design.Slug}{Path.GetExtension(design.R2Key)}";
        if (r2.IsEnabled && !string.IsNullOrWhiteSpace(design.R2Key))
        {
            return await r2.GetPresignedDownloadUrlAsync(design.R2Key, cancellationToken, fileName);
        }

        if (localFiles.FindDownloadPath(design.Id) is not null)
        {
            return $"{Request.Scheme}://{Request.Host}/api/designs/{design.Id}/file";
        }

        if (!string.IsNullOrWhiteSpace(design.R2Key))
        {
            return await r2.GetPresignedDownloadUrlAsync(design.R2Key, cancellationToken, fileName);
        }

        throw new InvalidOperationException("No hay archivo descargable para este diseño.");
    }

    private async Task<DesignDto> ToDtoAsync(Design d, float? similarity, CancellationToken cancellationToken)
    {
        var preview = await ResolvePreviewUrlAsync(d, cancellationToken);
        return new DesignDto(
            d.Id, d.Title, d.Slug, d.Description, d.Category, d.PriceUsd, d.CreditsCost,
            preview, d.CreatedAt, d.IsFreeDaily, similarity);
    }

    private async Task<string> ResolvePreviewUrlAsync(Design d, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(d.PreviewR2Key) && r2.IsEnabled)
        {
            var pub = r2.TryBuildPublicUrl(d.PreviewR2Key);
            if (!string.IsNullOrWhiteSpace(pub))
            {
                return pub;
            }

            // URL firmada fresca para <img> (bucket privado)
            return await r2.GetPresignedDownloadUrlAsync(
                d.PreviewR2Key,
                cancellationToken,
                lifetime: r2.PreviewUrlLifetime);
        }

        if (!string.IsNullOrWhiteSpace(d.PreviewUrl))
        {
            return d.PreviewUrl;
        }

        return $"/api/designs/{d.Id}/preview";
    }

    private static string? NormalizeSport(string? category)
    {
        var value = (category ?? "").Trim();
        if (value.Equals("Vóley", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Voley", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Voleibol", StringComparison.OrdinalIgnoreCase))
        {
            return "Vóley";
        }

        if (value.Equals("Fútbol", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Futbol", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Jersey", StringComparison.OrdinalIgnoreCase))
        {
            return "Fútbol";
        }

        return null;
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var normalized = new string(title.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        var slug = normalized.Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = $"diseno-{Guid.NewGuid():N}"[..20];
        }

        var candidate = slug;
        var i = 2;
        while (await db.Designs.AnyAsync(d => d.Slug == candidate, cancellationToken))
        {
            candidate = $"{slug}-{i++}";
        }

        return candidate;
    }
}
