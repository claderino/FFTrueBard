using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFTrueBard.Lessons;

public sealed record LessonTablatureEntry(int Exercise, int PdfPage, LessonTablatureCrop Crop);

/// <summary>Local book coordinates only, independent of guided cards and grading data.</summary>
public sealed class LessonTablatureIndex
{
    internal const string BundledResource = "FFTrueBard.Lessons.GuitarAerobics.tablature.json";
    private const long BundledPdfBytes = 3_381_197; // Skip hashing unrelated PDFs when no sidecar is present.
    private sealed record IndexFile(int SchemaVersion, string PdfSha256, LessonTablatureEntry[] Exercises);

    private readonly Dictionary<int, LessonTablatureEntry> entries;

    private LessonTablatureIndex(LessonTablatureEntry[] exercises) =>
        entries = exercises.ToDictionary(e => e.Exercise);

    public int Count => entries.Count;
    public LessonTablatureEntry? Find(int exercise) => entries.GetValueOrDefault(exercise);

    public static LessonTablatureIndex? LoadForPdf(string pdfPath, Action<string>? warn = null)
    {
        pdfPath = LessonLibrary.Normalize(pdfPath);
        if (!Path.GetExtension(pdfPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        var pdfInfo = new FileInfo(pdfPath);
        if (!pdfInfo.Exists) return null;

        string? digest = null;
        string PdfHash()
        {
            if (digest is not null) return digest;
            using var pdf = pdfInfo.OpenRead();
            return digest = Convert.ToHexString(SHA256.HashData(pdf));
        }

        var path = Path.ChangeExtension(pdfPath, ".tablature.json");
        if (File.Exists(path))
        {
            try
            {
                using var input = File.OpenRead(path);
                var local = Read(input);
                if (!local.PdfSha256.Equals(PdfHash(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The tablature index was made for a different PDF.");
                return new(local.Exercises);
            }
            catch (Exception e) when (warn is not null && IsIndexError(e))
            {
                warn($"Tablature index ignored: {e.Message}");
            }
        }

        if (pdfInfo.Length != BundledPdfBytes) return null;
        var bundled = ReadBundled();
        return bundled.PdfSha256.Equals(PdfHash(), StringComparison.OrdinalIgnoreCase)
            ? new(bundled.Exercises) : null;
    }

    internal static bool IsIndexError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;

    internal static LessonTablatureIndex BundledIndexForValidation() => new(ReadBundled().Exercises);

    private static IndexFile ReadBundled()
    {
        using var stream = typeof(LessonTablatureIndex).Assembly.GetManifestResourceStream(BundledResource)
            ?? throw new InvalidDataException("Built-in tablature index is missing.");
        var index = Read(stream, bundled: true);
        if (index.Exercises.Length != 365)
            throw new InvalidDataException("Built-in tablature index must cover all 365 exercises.");
        return index;
    }

    private static IndexFile Read(Stream input, bool bundled = false)
    {
        if (input.Length > 256 * 1024)
            throw new InvalidDataException("A tablature index must be at most 256 KiB.");
        var index = JsonSerializer.Deserialize<IndexFile>(input, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = bundled ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
        });
        if (index is not { SchemaVersion: 1, PdfSha256.Length: 64, Exercises.Length: >= 1 and <= 365 } ||
            !index.PdfSha256.All(Uri.IsHexDigit) ||
            index.Exercises.Any(e => e is null || e.Exercise is < 1 or > 365 ||
                e.PdfPage is < 1 or > 10000 || e.Crop is null) ||
            index.Exercises.Select(e => e.Exercise).Distinct().Count() != index.Exercises.Length)
            throw new InvalidDataException("Invalid tablature index: check the version, PDF hash, and unique exercise numbers.");
        foreach (var exercise in index.Exercises) exercise.Crop.Validate();
        return index;
    }
}
