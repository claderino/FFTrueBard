using System.Security.Cryptography;
using System.Text.Json;

namespace FFTrueBard.Lessons;

internal sealed record LessonScoreMark(double Start, double End, double X, double Y, double Width, double Height)
{
    public bool IsValid(double start, double end) =>
        double.IsFinite(Start) && double.IsFinite(End) && Start >= start && End > Start && End <= end &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

// Reviewed timings and note coordinates stay beside the user's lessons, never in the plugin.
internal sealed record LessonScorePlayhead(int SchemaVersion, int Exercise, int PdfPage, bool Reviewed,
    string AudioSha256, LessonScoreMark[] Notes)
{
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
            if (map is not { SchemaVersion: 1, Reviewed: true, AudioSha256.Length: 64, Notes.Length: >= 1 and <= 512 } ||
                map.Exercise != exercise || map.PdfPage != page || !map.AudioSha256.All(Uri.IsHexDigit))
                return null;
            var previous = start;
            foreach (var note in map.Notes)
            {
                if (note is null || !note.IsValid(previous, end ?? double.PositiveInfinity)) return null;
                previous = note.End;
            }
            using var audio = File.OpenRead(track);
            return Convert.ToHexString(SHA256.HashData(audio)).Equals(map.AudioSha256, StringComparison.OrdinalIgnoreCase)
                ? map : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public LessonScoreMark? At(double seconds) =>
        Notes.FirstOrDefault(note => note.Start <= seconds && seconds < note.End);
}
