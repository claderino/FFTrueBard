namespace FFTrueBard.Lessons;

internal sealed record TabPracticeFile(string Pdf, string? Midi, string? Guide = null)
{
    public string Title => Path.GetFileNameWithoutExtension(Pdf);

    public static TabPracticeFile[] Scan(string folder)
    {
        folder = LessonLibrary.Normalize(folder);
        if (folder.Length == 0 || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("Choose the local folder containing your TAB PDFs.");

        var files = Directory.EnumerateFiles(folder).Where(path => Path.GetExtension(path).ToLowerInvariant()
            is ".pdf" or ".mid" or ".midi").Take(1001).ToArray();
        if (files.Length > 1000)
            throw new InvalidDataException("Choose a folder with at most 1,000 PDF and MIDI files.");

        var midi = files.Where(path => !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToLookup(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        return files.Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var guide = Path.ChangeExtension(path, ".tab-guide.json");
                return new TabPracticeFile(path, midi[Path.GetFileNameWithoutExtension(path)].FirstOrDefault(),
                    File.Exists(guide) ? guide : null);
            })
            .ToArray();
    }
}
