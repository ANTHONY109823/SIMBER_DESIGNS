namespace SimberDesigns.Server.Services;

public sealed class LocalCatalogStorage(IWebHostEnvironment env)
{
    public string PreviewRoot => Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "catalog-previews");

    public string FileRoot => Path.Combine(env.ContentRootPath, "Uploads", "designs");

    public void EnsureFolders()
    {
        Directory.CreateDirectory(PreviewRoot);
        Directory.CreateDirectory(FileRoot);
    }

    public async Task<string> SavePreviewAsync(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        EnsureFolders();
        var ext = NormalizePreviewExtension(file.FileName);
        var name = $"{id:N}{ext}";
        var path = Path.Combine(PreviewRoot, name);
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, cancellationToken);
        return $"/catalog-previews/{name}";
    }

    public async Task<string> SaveDownloadAsync(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        EnsureFolders();
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".cdr";
        }

        var name = $"{id:N}{ext.ToLowerInvariant()}";
        var path = Path.Combine(FileRoot, name);
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, cancellationToken);
        return path;
    }

    public string? FindDownloadPath(Guid id)
    {
        if (!Directory.Exists(FileRoot))
        {
            return null;
        }

        return Directory.GetFiles(FileRoot, $"{id:N}.*").FirstOrDefault();
    }

    public void Delete(Guid id, string? previewUrl)
    {
        var download = FindDownloadPath(id);
        if (download is not null)
        {
            File.Delete(download);
        }

        if (!string.IsNullOrWhiteSpace(previewUrl) && previewUrl.StartsWith("/catalog-previews/", StringComparison.OrdinalIgnoreCase))
        {
            var preview = Path.Combine(PreviewRoot, Path.GetFileName(previewUrl));
            if (File.Exists(preview))
            {
                File.Delete(preview);
            }
        }
    }

    private static string NormalizePreviewExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".jpg";
    }
}
