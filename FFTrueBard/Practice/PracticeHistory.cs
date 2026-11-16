using System.Text.Json;

namespace FFTrueBard.Practice;

public sealed class PracticeSession
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public string FirstChord { get; set; } = "";
    public string SecondChord { get; set; } = "";
    public int Attempts { get; set; }
    public int Correct { get; set; }
    public int CompletedChecks { get; set; }
    public Dictionary<string, int> StringMisses { get; set; } = [];
    public string Pair => SecondChord.Length == 0 ? FirstChord : $"{FirstChord} / {SecondChord}";
}

public sealed class PracticeHistory
{
    private readonly string path;
    private PracticeSession? current;
    public List<PracticeSession> Sessions { get; private set; } = [];
    public string Status { get; private set; } = "History saves locally after each checked note.";
    private bool loadFailed;

    public PracticeHistory(string path)
    {
        this.path = path;
        if (!File.Exists(path))
            return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<PracticeSession>>(File.ReadAllText(path));
            if (loaded is null || loaded.Any(s => s is null || s.FirstChord is null || s.SecondChord is null || s.StringMisses is null))
                throw new InvalidDataException("Invalid history structure.");
            Sessions = loaded.TakeLast(200).ToList();
        }
        catch (Exception e)
        {
            loadFailed = true;
            Status = $"History could not be loaded; existing file will not be overwritten: {e.Message}";
        }
    }

    public void NewSession() => current = null;

    public void Observe(string first, string second, string chord, int stringNumber, bool correct, bool completed)
    {
        if (current is null || current.FirstChord != first || current.SecondChord != second)
        {
            current = new PracticeSession { FirstChord = first, SecondChord = second };
            Sessions.Add(current);
            if (Sessions.Count > 200)
                Sessions.RemoveAt(0);
        }
        current.Attempts++;
        if (correct)
            current.Correct++;
        else
        {
            var key = $"{chord}, string {stringNumber}";
            current.StringMisses[key] = current.StringMisses.GetValueOrDefault(key) + 1;
        }
        if (completed)
            current.CompletedChecks++;
        Save();
    }

    public (string First, string Second, string Reason) Recommend()
    {
        var weakest = Sessions.Where(s => s.Attempts > 0)
            .GroupBy(s => (s.FirstChord, s.SecondChord))
            .Select(g => new { g.Key, Attempts = g.Sum(s => s.Attempts), Misses = g.Sum(s => s.Attempts - s.Correct) })
            .Where(g => g.Attempts >= 5 && g.Misses > 0)
            .OrderByDescending(g => (double)g.Misses / g.Attempts).FirstOrDefault();
        if (weakest is not null)
            return (weakest.Key.FirstChord, weakest.Key.SecondChord,
                $"Highest observed miss rate: {weakest.Misses}/{weakest.Attempts} checked notes. Slow down and check each string.");
        var pair = new[] { ("Em", "Am"), ("C", "G"), ("G", "D"), ("Am", "C"), ("E", "A"), ("Dm", "Am") }
            .MinBy(p => Sessions.Where(s => s.FirstChord == p.Item1 && s.SecondChord == p.Item2).Sum(s => s.Attempts));
        return (pair.Item1, pair.Item2, "Try a less-practised chord pair; there are no established weak pairs yet.");
    }

    private void Save()
    {
        if (loadFailed)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(Sessions));
            File.Move(path + ".new", path, true);
            Status = $"Saved {Sessions.Count} sessions (last 200 retained).";
        }
        catch (Exception e)
        {
            Status = $"History save failed: {e.Message}";
        }
    }
}
