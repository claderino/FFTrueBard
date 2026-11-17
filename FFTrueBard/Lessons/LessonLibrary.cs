using System.Text.RegularExpressions;

namespace FFTrueBard.Lessons;

public sealed record LessonPaths(string Root, string Pdf, string Exercises, string Rhythm);
public sealed record LessonTrack(string Path, string Title, int Week = 0, int First = 0, int Last = 0,
    string Style = "", int Bpm = 0);
public sealed record LessonLibrary(LessonPaths Paths, IReadOnlyList<LessonTrack> Exercises,
    IReadOnlyList<LessonTrack> Rhythm, IReadOnlyList<string> Warnings)
{
    public static LessonLibrary Scan(LessonPaths requested)
    {
        var warnings = new List<string>();
        var root = Normalize(requested.Root);
        if (root.Length > 0 && !Directory.Exists(root))
            throw new DirectoryNotFoundException("The lesson root folder does not exist. Select a folder on this PC.");
        var exercises = Normalize(requested.Exercises);
        var rhythm = Normalize(requested.Rhythm);
        var pdf = Normalize(requested.Pdf);
        if (root.Length > 0)
        {
            exercises = exercises.Length > 0 ? exercises : FindFolder(root, "Exercises");
            rhythm = rhythm.Length > 0 ? rhythm : FindFolder(root, "Rhythm");
            if (pdf.Length == 0)
            {
                var candidates = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (candidates.Length == 1) pdf = candidates[0];
                else if (candidates.Length > 1) warnings.Add("Several PDFs found; select the lesson PDF explicitly.");
            }
        }
        if (pdf.Length > 0 && (!File.Exists(pdf) || !Path.GetExtension(pdf).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Lesson PDF is missing or is not a .pdf file.");
        if (root.Length == 0 && exercises.Length == 0 && rhythm.Length == 0 && pdf.Length == 0)
            warnings.Add("Select a local lesson root folder, or set the individual locations below.");
        return new(new(root, pdf, exercises, rhythm), Tracks(exercises, false, warnings), Tracks(rhythm, true, warnings), warnings);
    }

    public static string Normalize(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return "";
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use an absolute local file or folder path.");
        var full = Path.GetFullPath(path);
        if (new Uri(full).IsUnc)
            throw new ArgumentException("Use files stored on this PC, not a network share.");
        return full;
    }

    private static string FindFolder(string root, string suffix)
    {
        var matches = Directory.EnumerateDirectories(root).Where(p => Path.GetFileName(p)
            .EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(p).Equals(suffix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : root;
    }

    private static IReadOnlyList<LessonTrack> Tracks(string directory, bool rhythm, List<string> warnings)
    {
        if (directory.Length == 0) return [];
        if (!Directory.Exists(directory))
        {
            warnings.Add($"Missing {(rhythm ? "rhythm" : "exercise")} folder: {directory}");
            return [];
        }
        var files = Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p)
            .Equals(".mp3", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).Take(1001).ToArray();
        if (files.Length > 1000) warnings.Add("Only the first 1,000 MP3 files per folder are listed.");
        if (files.Length == 0) warnings.Add($"No MP3 files in the {(rhythm ? "rhythm" : "exercise")} folder. Subfolders are not scanned.");
        return files.Take(1000).Select(ParseTrack).ToArray();
    }

    public static LessonTrack ParseTrack(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var week = Regex.Match(name, @"Week-(\d{1,2})_Exercises-(\d{1,3})[–-](\d{1,3})$", RegexOptions.IgnoreCase);
        if (week.Success)
        {
            var w = int.Parse(week.Groups[1].Value);
            var first = int.Parse(week.Groups[2].Value);
            var last = int.Parse(week.Groups[3].Value);
            if (w is >= 1 and <= 52 && first >= 1 && last >= first && last <= 365)
                return new(path, $"Week {w:00} — exercises {first}–{last}", w, first, last);
        }
        var beat = Regex.Match(name, @"^\d+-(.+)-(\d{2,3})bpm$", RegexOptions.IgnoreCase);
        if (beat.Success && int.TryParse(beat.Groups[2].Value, out var bpm) && bpm > 0)
            return new(path, name.Replace('_', ' '), Style: beat.Groups[1].Value.Replace('_', ' '), Bpm: bpm);
        return new(path, name.Replace('_', ' '));
    }
}
