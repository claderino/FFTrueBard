using System.Text.Json;

namespace FFTrueBard.Lessons;

public sealed record SequenceNote(int Midi, int? String = null, int? Fret = null)
{
    private static readonly int[] OpenMidi = [64, 59, 55, 50, 45, 40];
    public bool Valid => Midi is >= 40 and <= 87 &&
        (String is null && Fret is null || String is >= 1 and <= 6 && Fret is >= 0 and <= 24 &&
            OpenMidi[String.Value - 1] + Fret == Midi);
    public string Name => NoteName(Midi);
    public string Hint => String is null ? "Pitch only" : $"String {String}, fret {Fret} (guide only)";
    public static string NoteName(int midi)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return midi is >= 0 and <= 127 ? $"{names[midi % 12]}{midi / 12 - 1}" : "--";
    }
}

/// <summary>User-owned pitch data, loaded locally; no course transcriptions are bundled.</summary>
public sealed record LessonSequence(int SchemaVersion, int Exercise, string Title, string Source,
    SequenceNote[] Notes)
{
    public void Validate(int exercise)
    {
        if (SchemaVersion != 1 || Exercise is < 1 or > 365 || Exercise != exercise ||
            string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || Source is null || Source.Length > 500 ||
            Notes is null || Notes.Length is < 1 or > 512 || Notes.Any(n => n is null || !n.Valid))
            throw new InvalidDataException("Unsupported pitch-sequence data: check schema, exercise, MIDI pitches (40–87), and optional standard-tuning string/fret pairs.");
    }

    public static LessonSequence Load(string path, int exercise)
    {
        path = LessonLibrary.Normalize(path);
        if (!path.EndsWith(".notes.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .notes.json pitch-sequence file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 256 * 1024) throw new InvalidDataException("Pitch-sequence files must be at most 256 KiB.");
        var sequence = JsonSerializer.Deserialize<LessonSequence>(input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty pitch-sequence data.");
        sequence.Validate(exercise);
        return sequence;
    }

    public static string DefaultPath(string root, string pdf, int exercise)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? Path.GetDirectoryName(LessonLibrary.Normalize(pdf)) : LessonLibrary.Normalize(root);
        return string.IsNullOrWhiteSpace(folder) ? "" : Path.Combine(folder, "fftruebard-lessons", $"exercise-{exercise:000}.notes.json");
    }
}
