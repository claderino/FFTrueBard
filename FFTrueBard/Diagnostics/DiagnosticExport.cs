using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FFTrueBard.Diagnostics;

public sealed record ExportSource(string RelativePath, long Bytes, DateTime LastWriteUtc);
public sealed record ExportItem(string Name, byte[] Content)
{
    public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Content));
}
public sealed record ExportPreview(DateTime CreatedUtc, IReadOnlyList<ExportItem> Items)
{
    public long TotalBytes => Items.Sum(i => (long)i.Content.Length);
}

/// <summary>Allowlisted local files only. Export writes the exact bytes frozen in the preview.</summary>
public sealed class DiagnosticExport(string root)
{
    public const long MaxBytes = 50L * 1024 * 1024;

    public IReadOnlyList<ExportSource> ListSources()
    {
        var files = new List<ExportSource>();
        foreach (var directory in new[] { "diagnostics", "training-reports" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var relative = $"{directory}/{Path.GetFileName(file)}";
                if (!Allowed(relative) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var info = new FileInfo(file);
                files.Add(new(relative, info.Length, info.LastWriteTimeUtc));
            }
        }
        return files.OrderByDescending(f => f.LastWriteUtc).Take(100).ToArray();
    }

    private static bool Allowed(string relative)
    {
        var parts = relative.Split('/');
        return parts.Length == 2 && !parts[1].Contains('\\') && !parts[1].Contains(':') &&
            parts[1] is not ("." or "..") &&
            (parts[0] == "diagnostics" && parts[1].EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
             parts[0] == "training-reports" && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    public ExportPreview Preview(IEnumerable<string> selected, string sessionJson, string settingsJson)
    {
        var items = new List<ExportItem>
        {
            new("session-info.json", Encoding.UTF8.GetBytes(sessionJson)),
            new("settings.json", Encoding.UTF8.GetBytes(settingsJson)),
        };
        long total = items.Sum(i => (long)i.Content.Length);
        foreach (var relative in selected.Distinct(StringComparer.Ordinal))
        {
            if (!Allowed(relative))
                throw new InvalidDataException("Only displayed diagnostic logs and training reports can be exported.");
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(path)!;
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked files/directories are not exported.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxBytes - total)
                throw new InvalidDataException("Selected files exceed the 50 MiB uncompressed export limit.");
            var content = new byte[checked((int)input.Length)];
            input.ReadExactly(content);
            total += content.Length;
            items.Add(new(relative, content));
        }
        if (total > MaxBytes)
            throw new InvalidDataException("Export exceeds 50 MiB.");
        return new(DateTime.UtcNow, items);
    }

    public string CreateArchive(ExportPreview preview, bool reviewed)
    {
        if (!reviewed)
            throw new InvalidOperationException("Review and approve the contents preview before creating an archive.");
        if (preview.TotalBytes > MaxBytes || !preview.Items.Any(i => i.Name == "session-info.json") ||
            !preview.Items.Any(i => i.Name == "settings.json") ||
            preview.Items.Select(i => i.Name).Distinct().Count() != preview.Items.Count ||
            preview.Items.Any(i => i.Name is not ("session-info.json" or "settings.json") && !Allowed(i.Name)))
            throw new InvalidDataException("Invalid export preview.");
        var directory = Path.Combine(root, "exports");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A linked export directory is not supported.");
        var path = Path.Combine(directory, $"fftruebard-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var item in preview.Items)
        {
            using var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal).Open();
            entry.Write(item.Content);
        }
        return path;
    }
}
