using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFTrueBard.Lessons;

internal sealed record LessonScoreMark(double Start, double End, double X, double Y, double Width, double Height, int Pass = 0)
{
    public bool IsValid(double start, double end) =>
        double.IsFinite(Start) && double.IsFinite(End) && Start >= start && End > Start && End <= end &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

// Bundled maps contain timing and position data only. Local maps can override them.
internal sealed record LessonScorePlayhead(int SchemaVersion, int Exercise, int PdfPage, bool Reviewed,
    string AudioSha256, LessonScoreMark[] Notes)
{
    internal const string BundledPrefix = "FFTrueBard.Lessons.Indexes.guitar-aerobics-";
    internal const string BundledSuffix = ".playhead.json";
    internal sealed record BundledMap(string PdfSha256, [property: JsonRequired] int CueStartSeconds,
        [property: JsonRequired] int? CueEndSeconds, LessonScorePlayhead Map, double? MeasuredEndSeconds = null)
    {
        internal double? PlaybackEndSeconds => MeasuredEndSeconds ?? CueEndSeconds;

        internal bool IsValid(int exercise) =>
            PdfSha256 is { Length: 64 } && PdfSha256.All(Uri.IsHexDigit) && CueStartSeconds >= 0 &&
            (CueEndSeconds is null || CueEndSeconds > CueStartSeconds) &&
            (MeasuredEndSeconds is null ||
                CueEndSeconds is { } printed && MeasuredEndSeconds is { } measured &&
                double.IsFinite(measured) && measured > printed && measured <= printed + 2d &&
                Map?.Notes?.LastOrDefault()?.End > printed) &&
            Map is { PdfPage: >= 1 } && Map.IsValid(exercise, Map.PdfPage, CueStartSeconds, PlaybackEndSeconds);
    }

    internal sealed record ReaderTiming(LessonScorePlayhead? Map, string? Problem, double? MeasuredEndSeconds = null);

    private static readonly JsonSerializerOptions LocalJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string DefaultPath(string root, string pdf, int exercise)
    {
        var folder = Path.GetDirectoryName(LessonSequence.DefaultPath(root, pdf, exercise));
        return folder is null ? "" : Path.Combine(folder, $"exercise-{exercise:000}.playhead.json");
    }

    public static LessonScorePlayhead? TryLoad(string path, int exercise, int page, string track, double start, int? end)
        => ReadLocal(path, null, exercise, page, track, start, end)?.Map;

    private static ReaderTiming? ReadLocal(string path, string? pdf, int exercise, int page, string track, double start, int? end)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 64 * 1024) return null;
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (pdf is not null && root.EnumerateObject().Any(p => p.Name.Equals("map", StringComparison.OrdinalIgnoreCase)))
            {
                var envelope = root.Deserialize<BundledMap>(EnvelopeJson);
                if (envelope?.IsValid(exercise) != true) return null;
                var result = MatchEnvelope(envelope, pdf, page, track, start, end);
                return result.Map is null ? null : result;
            }
            var map = root.Deserialize<LessonScorePlayhead>(LocalJson);
            return map is not null && map.IsValid(exercise, page, start, end) && FileMatches(track, map.AudioSha256)
                ? new(map, null) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static ReaderTiming FindForReader(
        string root, string pdf, int exercise, int page, string track, double start, int? end)
    {
        var local = FindLocal(root, pdf, exercise, page, track, start, end);
        if (local.Map is not null) return local;
        try
        {
            var bundled = ReadBundled(exercise);
            if (bundled is null) return local;
            return MatchEnvelope(bundled, pdf, page, track, start, end);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(null, "Could not verify the built-in note timings.");
        }
    }

    private static ReaderTiming MatchEnvelope(BundledMap envelope, string pdf, int page, string track, double start, int? end)
    {
        if (envelope.Map.PdfPage != page || start != envelope.CueStartSeconds ||
            end != envelope.CueEndSeconds || !FileMatches(pdf, envelope.PdfSha256))
            return new(null, "Built-in note timings need the supported Guitar Aerobics PDF and cue range.");
        if (!FileMatches(track, envelope.Map.AudioSha256))
            return new(null, "Built-in note timings need the matching weekly MP3. This file can still play and scrub by audio time.");
        return new(envelope.Map, null, envelope.MeasuredEndSeconds);
    }

    internal static BundledMap? ReadBundled(int exercise)
    {
        if (exercise is < 1 or > 365) return null;
        using var input = typeof(LessonScorePlayhead).Assembly
            .GetManifestResourceStream(FormattableString.Invariant($"{BundledPrefix}{exercise:000}{BundledSuffix}"));
        if (input is null) return null;
        var bundle = JsonSerializer.Deserialize<BundledMap>(input, EnvelopeJson);
        if (bundle?.IsValid(exercise) != true)
            throw new InvalidDataException($"Invalid built-in timing map for Exercise {exercise}.");
        return bundle;
    }

    private bool IsValid(int exercise, int page, double start, double? end)
    {
        if (this is not { SchemaVersion: 1 or 2, Reviewed: true, AudioSha256.Length: 64, Notes.Length: >= 1 and <= 512 } ||
            Exercise != exercise || PdfPage != page || !AudioSha256.All(Uri.IsHexDigit))
            return false;
        var previous = start;
        var pass = 0;
        var x = double.NegativeInfinity;
        for (var i = 0; i < Notes.Length; i++)
        {
            var note = Notes[i];
            if (note is null || !note.IsValid(previous, end ?? double.PositiveInfinity)) return false;
            if (note.Pass != pass)
            {
                if (SchemaVersion != 2 || i == 0 || note.Pass != pass + 1) return false;
                pass = note.Pass;
                x = double.NegativeInfinity;
            }
            var center = note.X + note.Width / 2;
            if (center <= x) return false;
            x = center;
            previous = note.End;
        }
        return SchemaVersion == 1 || pass > 0;
    }

    private static bool FileMatches(string path, string hash)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    public static ReaderTiming FindLocal(
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
            if (ReadLocal(path, pdf, exercise, page, track, start, end) is { } timing)
                return timing;
        }
        return new(null, foundFile
            ? "Local note timings couldn't be used. Check that they match this exercise and MP3."
            : $"No built-in note timings for this exercise. A reviewed exercise-{exercise:000}.playhead.json can be loaded from fftruebard-lessons.");
    }

    public LessonScoreMark? At(double seconds) =>
        Notes.FirstOrDefault(note => note.Start <= seconds && seconds < note.End);
}
