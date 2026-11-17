using System.Text.Json;

namespace FFTrueBard.Lessons;

public sealed record LessonEntry(bool Completed, string Notes);
public sealed record LessonProgressData(int SchemaVersion, Dictionary<int, LessonEntry> Exercises);

public sealed class LessonProgress(string path)
{
    private Dictionary<int, LessonEntry> entries = [];
    public string Status { get; private set; } = "";
    public int Completed => entries.Values.Count(e => e.Completed);
    public LessonEntry Get(int exercise) => entries.GetValueOrDefault(exercise, new(false, ""));

    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Progress file is too large.");
            var data = JsonSerializer.Deserialize<LessonProgressData>(File.ReadAllText(path));
            if (data is null || data.SchemaVersion != 1 || data.Exercises is null ||
                data.Exercises.Any(p => p.Key is < 1 or > 365 || p.Value is null || p.Value.Notes is null || p.Value.Notes.Length > 2000))
                throw new InvalidDataException("Unsupported lesson progress data.");
            entries = data.Exercises;
            Status = "Loaded local lesson progress.";
        }
        catch (Exception e) { Status = $"Could not load progress; original file kept: {e.Message}"; }
    }

    public bool Save(int exercise, bool completed, string notes)
    {
        if (exercise is < 1 or > 365) return false;
        var updated = new Dictionary<int, LessonEntry>(entries)
        {
            [exercise] = new(completed, notes.Length > 2000 ? notes[..2000] : notes),
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Preserve the last on-disk state, including unreadable data, before replacement.
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(new LessonProgressData(1, updated),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".new", path, true);
            entries = updated;
            Status = "Saved local progress and notes. Completion is self-reported, not graded.";
            return true;
        }
        catch (Exception e) { Status = $"Could not save progress: {e.Message}"; return false; }
    }
}
