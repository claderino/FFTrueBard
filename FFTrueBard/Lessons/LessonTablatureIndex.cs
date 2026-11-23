using System.Security.Cryptography;
using System.Text.Json;

namespace FFTrueBard.Lessons;

public sealed record LessonTablatureEntry(int Exercise, int PdfPage, LessonTablatureCrop Crop);

/// <summary>Local book coordinates only, independent of guided cards and grading data.</summary>
public sealed class LessonTablatureIndex
{
    private sealed record IndexFile(int SchemaVersion, string PdfSha256, LessonTablatureEntry[] Exercises);

    private readonly Dictionary<int, LessonTablatureEntry> entries;

    private LessonTablatureIndex(LessonTablatureEntry[] exercises) =>
        entries = exercises.ToDictionary(e => e.Exercise);

    public int Count => entries.Count;
    public LessonTablatureEntry? Find(int exercise) => entries.GetValueOrDefault(exercise);

    public static LessonTablatureIndex? LoadForPdf(string pdfPath)
    {
        pdfPath = LessonLibrary.Normalize(pdfPath);
        if (!Path.GetExtension(pdfPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.ChangeExtension(pdfPath, ".tablature.json");
        if (!File.Exists(path)) return null;

        using var input = File.OpenRead(path);
        if (input.Length > 256 * 1024)
            throw new InvalidDataException("A tablature index must be at most 256 KiB.");
        var index = JsonSerializer.Deserialize<IndexFile>(input, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (index is not { SchemaVersion: 1, PdfSha256.Length: 64, Exercises.Length: >= 1 and <= 365 } ||
            !index.PdfSha256.All(Uri.IsHexDigit) ||
            index.Exercises.Any(e => e is null || e.Exercise is < 1 or > 365 ||
                e.PdfPage is < 1 or > 10000 || e.Crop is null) ||
            index.Exercises.Select(e => e.Exercise).Distinct().Count() != index.Exercises.Length)
            throw new InvalidDataException("Invalid tablature index: check the version, PDF hash, and unique exercise numbers.");
        foreach (var exercise in index.Exercises) exercise.Crop.Validate();

        using var pdf = File.OpenRead(pdfPath);
        var hash = Convert.ToHexString(SHA256.HashData(pdf));
        if (!hash.Equals(index.PdfSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The tablature index was made for a different PDF.");
        return new(index.Exercises);
    }
}
