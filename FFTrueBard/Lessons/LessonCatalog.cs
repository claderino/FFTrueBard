using System.Text.Json;

namespace FFTrueBard.Lessons;

/// <summary>Local, declarative lesson cards. Media and notation stay in user-owned files.</summary>
public sealed record LessonCatalog(int SchemaVersion, string Title, string Source, LessonExercise[] Exercises)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || !Text(Title, 200, true) || !Text(Source, 500) ||
            Exercises is null || Exercises.Length is < 1 or > 365 ||
            Exercises.Any(e => e is null) || Exercises.Select(e => e.Exercise).Distinct().Count() != Exercises.Length)
            throw new InvalidDataException("Unsupported lesson catalog: use schema 1 and 1–365 unique exercise IDs.");
        foreach (var exercise in Exercises) exercise.Validate();
    }

    internal static bool Text(string? value, int max, bool required = false) =>
        value is not null && value.Length <= max && (!required || !string.IsNullOrWhiteSpace(value));

    public static LessonCatalog Load(string path)
    {
        path = LessonLibrary.Normalize(path);
        if (!path.EndsWith(".course.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .course.json exercise catalog.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Lesson catalogs must be at most 2 MiB.");
        var catalog = JsonSerializer.Deserialize<LessonCatalog>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty lesson catalog.");
        catalog.Validate();
        return catalog;
    }

    public static string DefaultPath(string root, string pdf)
    {
        var sequence = LessonSequence.DefaultPath(root, pdf, 1);
        return sequence.Length == 0 ? "" : Path.Combine(Path.GetDirectoryName(sequence)!, "exercises.course.json");
    }

    public static bool IsFileName(string name, string suffix) => Text(name, 240, true) &&
        name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length;

    public IEnumerable<LessonExercise> Filter(int week = 0, string technique = "") => Exercises
        .Where(e => (week == 0 || e.Week == week) && (technique.Length == 0 ||
            e.Technique.Equals(technique, StringComparison.OrdinalIgnoreCase))).OrderBy(e => e.Exercise);
}

public sealed record LessonExercise
{
    public int Exercise { get; init; }
    public string Title { get; init; } = "";
    public string Technique { get; init; } = "";
    public string[] Steps { get; init; } = [];
    public string[] SelfChecks { get; init; } = [];
    public int? PdfPage { get; init; }
    public LessonTablatureCrop? TablatureCrop { get; init; }
    public string PrintedPage { get; init; } = "";
    public int? MinBpm { get; init; }
    public int? MaxBpm { get; init; }
    public string RhythmGuide { get; init; } = "";
    public string Mode { get; init; } = "guided";
    public string PitchSequenceFile { get; init; } = "";
    public LessonAudioCue? Audio { get; init; }
    public int Week => Math.Min(52, (Exercise - 1) / 7 + 1);

    public void Validate()
    {
        if (Exercise is < 1 or > 365 || !LessonCatalog.Text(Title, 200, true) ||
            !LessonCatalog.Text(Technique, 100, true) || !Lines(Steps, 20, 1000, true) ||
            !Lines(SelfChecks, 20, 500, false) || PdfPage is < 1 or > 10000 ||
            !LessonCatalog.Text(PrintedPage, 40) || !LessonCatalog.Text(RhythmGuide, 300) ||
            (MinBpm is null) != (MaxBpm is null) || MinBpm is < 20 or > 400 ||
            MaxBpm is < 20 or > 400 || MaxBpm < MinBpm || Mode is not ("guided" or "pitch-order") ||
            PitchSequenceFile is null || PitchSequenceFile.Length > 0 &&
                (Mode != "pitch-order" || !LessonCatalog.IsFileName(PitchSequenceFile, ".notes.json")))
            throw new InvalidDataException($"Invalid lesson card #{Exercise}: check steps, pages, tempo, practice mode, and file references.");
        Audio?.Validate();
        if (TablatureCrop is { } crop)
        {
            if (PdfPage is null)
                throw new InvalidDataException($"Lesson card #{Exercise}: a tablature crop requires a PDF page.");
            crop.Validate();
        }
    }

    private static bool Lines(string[]? lines, int count, int length, bool required) =>
        lines is not null && lines.Length <= count && (!required || lines.Length > 0) &&
        lines.All(s => LessonCatalog.Text(s, length, true));

    public string SequencePath(string catalogPath)
    {
        Validate();
        if (Mode != "pitch-order") return "";
        var name = PitchSequenceFile.Length > 0 ? PitchSequenceFile : $"exercise-{Exercise:000}.notes.json";
        return Path.Combine(Path.GetDirectoryName(LessonLibrary.Normalize(catalogPath))!, name);
    }
}

/// <summary>Optional rectangle in page fractions, measured from the top-left.</summary>
public sealed record LessonTablatureCrop(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) ||
            !double.IsFinite(Height) || X < 0 || Y < 0 || Width < 0.01 || Height < 0.01 ||
            X + Width > 1 || Y + Height > 1)
            throw new InvalidDataException("A tablature crop needs positive dimensions and must fit inside the PDF page.");
    }
}

public sealed record LessonAudioCue(string Track, double StartSeconds, double? EndSeconds = null)
{
    public void Validate()
    {
        if (Track is null || Track.Length > 0 && !LessonCatalog.IsFileName(Track, ".mp3") ||
            !double.IsFinite(StartSeconds) || StartSeconds < 0 || StartSeconds > 86400 ||
            EndSeconds is { } end && (!double.IsFinite(end) || end - StartSeconds < 0.1 || end > 86400))
            throw new InvalidDataException("Invalid lesson audio cue: use a direct MP3 filename and a finite, ordered time range.");
    }

    public (double Start, double End) Within(double duration)
    {
        Validate();
        var end = EndSeconds ?? duration;
        if (!double.IsFinite(duration) || duration <= 0 || StartSeconds >= duration ||
            end > duration || end - StartSeconds < 0.1)
            throw new InvalidDataException("Lesson cue is outside this MP3. Check the local catalog or seek manually.");
        return (StartSeconds, end);
    }

    public LessonTrack ResolveTrack(LessonLibrary library, int exercise)
    {
        Validate();
        var matches = library.Exercises.Where(t => Track.Length > 0
            ? Path.GetFileName(t.Path).Equals(Track, StringComparison.OrdinalIgnoreCase)
            : exercise >= t.First && exercise <= t.Last).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] :
            throw new InvalidDataException("The cue needs exactly one matching MP3 in the scanned exercise folder.");
    }
}
