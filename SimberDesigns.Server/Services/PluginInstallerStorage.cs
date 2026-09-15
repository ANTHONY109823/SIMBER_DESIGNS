using SimberDesigns.Licensing;

namespace SimberDesigns.Server.Services;

public sealed class PluginInstallerStorage(IWebHostEnvironment env)
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

    public string FilePath(string edition)
        => Path.Combine(Root, edition.Equals(LicenseProgram.Illustrator, StringComparison.OrdinalIgnoreCase)
            ? "SIMBER-ILLUSTRATOR.exe"
            : "SIMBER-COREL.exe");

    public string DownloadName(string edition)
        => edition.Equals(LicenseProgram.Illustrator, StringComparison.OrdinalIgnoreCase)
            ? "SIMBER DESIGNS ILLUSTRATOR.exe"
            : "SIMBER DESIGNS COREL.exe";

    public bool Exists(string edition) => File.Exists(FilePath(edition));

    public FileInfo? Info(string edition)
    {
        var path = FilePath(edition);
        return File.Exists(path) ? new FileInfo(path) : null;
    }

    public async Task SaveAsync(string edition, IFormFile file, CancellationToken cancellationToken)
    {
        EnsureFolder();
        var path = FilePath(edition);
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, cancellationToken);
    }
}
