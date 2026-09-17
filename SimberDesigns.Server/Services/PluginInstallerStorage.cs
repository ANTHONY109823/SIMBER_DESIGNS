using SimberDesigns.Licensing;

namespace SimberDesigns.Server.Services;

/// <summary>
/// Instaladores .exe: R2 en producción; disco local solo con UseFakeClient.
/// </summary>
public sealed class PluginInstallerStorage(
    IWebHostEnvironment env,
    ICloudflareR2Service r2)
{
    public string Root => Path.Combine(env.ContentRootPath, "Uploads", "installers");

    public void EnsureFolder() => Directory.CreateDirectory(Root);

    public static bool TryParseEdition(string? raw, out string edition)
    {
        edition = LicenseProgram.Normalizar(raw);
        if (edition is LicenseProgram.Corel or LicenseProgram.Illustrator)
        {
            return true;
        }

        var key = (raw ?? "").Trim().ToLowerInvariant();
        if (key is "corel" or "coreldraw")
        {
            edition = LicenseProgram.Corel;
            return true;
        }

        if (key is "ilus" or "illustrator" or "ilustrator")
        {
            edition = LicenseProgram.Illustrator;
            return true;
        }

        edition = "";
        return false;
    }

    public string R2ObjectKey(string edition)
        => edition.Equals(LicenseProgram.Illustrator, StringComparison.OrdinalIgnoreCase)
            ? "installers/SIMBER-ILLUSTRATOR.exe"
            : "installers/SIMBER-COREL.exe";

    public string FilePath(string edition)
        => Path.Combine(Root, edition.Equals(LicenseProgram.Illustrator, StringComparison.OrdinalIgnoreCase)
            ? "SIMBER-ILLUSTRATOR.exe"
            : "SIMBER-COREL.exe");

    public string DownloadName(string edition)
        => edition.Equals(LicenseProgram.Illustrator, StringComparison.OrdinalIgnoreCase)
            ? "SIMBER DESIGNS ILLUSTRATOR.exe"
            : "SIMBER DESIGNS COREL.exe";

    public async Task<bool> ExistsAsync(string edition, CancellationToken cancellationToken = default)
    {
        if (r2.IsEnabled)
        {
            return await r2.ExistsAsync(R2ObjectKey(edition), cancellationToken);
        }

        return File.Exists(FilePath(edition));
    }

    public bool Exists(string edition)
        => ExistsAsync(edition).GetAwaiter().GetResult();

    public async Task<(long Length, DateTime? LastWriteUtc)?> InfoAsync(
        string edition,
        CancellationToken cancellationToken = default)
    {
        if (r2.IsEnabled)
        {
            var info = await r2.GetObjectInfoAsync(R2ObjectKey(edition), cancellationToken);
            return info is null ? null : (info.Value.Length, info.Value.LastModified);
        }

        var path = FilePath(edition);
        if (!File.Exists(path))
        {
            return null;
        }

        var fi = new FileInfo(path);
        return (fi.Length, fi.LastWriteTimeUtc);
    }

    public FileInfo? Info(string edition)
    {
        var path = FilePath(edition);
        return File.Exists(path) ? new FileInfo(path) : null;
    }

    public async Task SaveAsync(string edition, IFormFile file, CancellationToken cancellationToken)
    {
        if (r2.IsEnabled)
        {
            await using var stream = file.OpenReadStream();
            await r2.UploadAsync(
                R2ObjectKey(edition),
                stream,
                "application/octet-stream",
                cancellationToken);
            return;
        }

        EnsureFolder();
        var path = FilePath(edition);
        await using var local = File.Create(path);
        await file.CopyToAsync(local, cancellationToken);
    }

    public async Task<string?> GetDownloadUrlAsync(string edition, CancellationToken cancellationToken)
    {
        if (!r2.IsEnabled)
        {
            return null;
        }

        if (!await r2.ExistsAsync(R2ObjectKey(edition), cancellationToken))
        {
            return null;
        }

        return await r2.GetPresignedDownloadUrlAsync(
            R2ObjectKey(edition),
            cancellationToken,
            DownloadName(edition));
    }
}
