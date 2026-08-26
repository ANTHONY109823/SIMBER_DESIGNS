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
    IDownloadLimitService downloadLimits) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<DesignDto>>> List(
        [FromQuery] string? category,
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        var query = db.Designs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category) && !string.Equals(category, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(d => d.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(d => d.Title.Contains(term) || (d.Description != null && d.Description.Contains(term)));
        }

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DesignDto(d.Id, d.Title, d.Slug, d.Description, d.Category, d.PriceUsd, d.CreditsCost, d.PreviewUrl, d.CreatedAt, null))
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpPost("generate-upload-url")]
    public async Task<ActionResult<PresignedUrlResponse>> GetUploadUrl(
        [FromQuery] string fileName,
        [FromQuery] string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(contentType))
        {
            return BadRequest("El nombre de archivo y Content-Type son obligatorios.");
        }

        var safeName = Path.GetFileName(fileName);
        var fileKey = $"designs/{Guid.NewGuid():N}_{safeName}";
        var uploadUrl = await r2.GetPresignedUploadUrlAsync(fileKey, contentType, cancellationToken);
        return Ok(new PresignedUrlResponse(uploadUrl, fileKey));
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
            SELECT id, title, slug, description, category, price_usd AS PriceUsd, credits_cost AS CreditsCost,
                   preview_url AS PreviewUrl, created_at AS CreatedAt,
                   (1 - (embedding <=> @Embedding))::real AS Similarity
            FROM designs
            WHERE embedding IS NOT NULL
            ORDER BY embedding <=> @Embedding
            LIMIT 8
            """;

        var rows = await connection.QueryAsync<DesignDto>(sql, new { Embedding = vector });
        return Ok(rows.AsList());
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

        var evaluation = await downloadLimits.EvaluateAsync(userId.Value, cancellationToken);
        var purchased = await db.CreditTransactions.AnyAsync(
            c => c.UserId == userId && c.DesignId == id && c.TxType == CreditTxTypes.PurchaseDesign,
            cancellationToken);
        var bought = await db.Transactions.AnyAsync(
            t => t.UserId == userId && t.DesignId == id && t.Status == TransactionStatuses.Completed,
            cancellationToken);

        var hasAccess = purchased || bought;
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

        var url = await r2.GetPresignedDownloadUrlAsync(design.R2Key, cancellationToken);
        return Ok(new DownloadResponse(url, DateTime.UtcNow.Add(r2.UrlLifetime), remaining));
    }
}
