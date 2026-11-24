using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFTrueBard.Lessons;

internal sealed record LessonScoreMark(double Start, double End, double X, double Y, double Width, double Height)
{
    public bool IsValid(double start, double end) =>
        double.IsFinite(Start) && double.IsFinite(End) && Start >= start && End > Start && End <= end &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

// The bundled map contains timing and position data only. Local maps can override it.
internal sealed record LessonScorePlayhead(int SchemaVersion, int Exercise, int PdfPage, bool Reviewed,
    string AudioSha256, LessonScoreMark[] Notes)
{
    internal const string BundledResource = "FFTrueBard.Lessons.GuitarAerobics.Exercise001.playhead.json";
    internal sealed record BundledMap(string PdfSha256, LessonScorePlayhead Map);

    public static string DefaultPath(string root, string pdf, int exercise)
    {
        var folder = Path.GetDirectoryName(LessonSequence.DefaultPath(root, pdf, exercise));
        return folder is null ? "" : Path.Combine(folder, $"exercise-{exercise:000}.playhead.json");
    }

    public static LessonScorePlayhead? TryLoad(string path, int exercise, int page, string track, double start, int? end)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 64 * 1024) return null;
            var map = JsonSerializer.Deserialize<LessonScorePlayhead>(input,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return map is not null && map.IsValid(exercise, page, start, end) && FileMatches(track, map.AudioSha256) ? map : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static (LessonScorePlayhead? Map, string? Problem) FindForReader(
        string root, string pdf, int exercise, int page, string track, double start, int? end)
    {
        var local = FindLocal(root, pdf, exercise, page, track, start, end);
        if (local.Map is not null || exercise != 1) return local;
        try
        {
            var bundled = ReadBundled();
            if (!bundled.Map.IsValid(exercise, page, start, end) || !FileMatches(pdf, bundled.PdfSha256))
                return (null, "Built-in note timings need the supported Guitar Aerobics PDF and cue range.");
            if (!FileMatches(track, bundled.Map.AudioSha256))
                return (null, "Built-in Exercise 1 timing needs the matching Week 1 MP3. This file can still play and scrub by audio time.");
            return (bundled.Map, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return (null, "Could not verify the built-in Exercise 1 note timings.");
        }
    }

    internal static BundledMap ReadBundled()
    {
        using var input = typeof(LessonScorePlayhead).Assembly.GetManifestResourceStream(BundledResource)
            ?? throw new InvalidDataException("Built-in Exercise 1 timing map is missing.");
        var bundle = JsonSerializer.Deserialize<BundledMap>(input, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        });
        if (bundle is not { PdfSha256.Length: 64, Map.Notes.Length: 32 } || !bundle.PdfSha256.All(Uri.IsHexDigit) ||
            !bundle.Map.IsValid(1, 7, 0, 11))
            throw new InvalidDataException("Invalid built-in Exercise 1 timing map.");
        return bundle;
    }

    private bool IsValid(int exercise, int page, double start, int? end)
    {
        if (this is not { SchemaVersion: 1, Reviewed: true, AudioSha256.Length: 64, Notes.Length: >= 1 and <= 512 } ||
            Exercise != exercise || PdfPage != page || !AudioSha256.All(Uri.IsHexDigit))
            return false;
        var previous = start;
        foreach (var note in Notes)
        {
            if (note is null || !note.IsValid(previous, end ?? double.PositiveInfinity)) return false;
            previous = note.End;
        }
        return true;
    }

    private static bool FileMatches(string path, string hash)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    public static (LessonScorePlayhead? Map, string? Problem) FindLocal(
        string root, string pdf, int exercise, int page, string track, double start, int? end)
    {
        var audioFolder = Path.GetDirectoryName(track);
        var audioParent = Path.GetDirectoryName(audioFolder);
        string[] candidates =
        [
            DefaultPath(root, pdf, exercise),
            DefaultPath("", pdf, exercise),
            DefaultPath(audioParent ?? "", pdf, exercise),
            DefaultPath(audioFolder ?? "", pdf, exercise),
        ];
        var foundFile = false;
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            foundFile = true;
            if (TryLoad(path, exercise, page, track, start, end) is { } map)
                return (map, null);
        }
        return (null, foundFile
            ? "Local note timings couldn't be used. Check that they match this exercise and MP3."
            : $"No reviewed exercise-{exercise:000}.playhead.json found in fftruebard-lessons.");
    }

    public LessonScoreMark? At(double seconds) =>
        Notes.FirstOrDefault(note => note.Start <= seconds && seconds < note.End);
}
