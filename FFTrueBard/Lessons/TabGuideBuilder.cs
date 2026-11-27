namespace FFTrueBard.Lessons;

internal static class TabGuideBuilder
{
    internal sealed record Column(double X, double Width, int[] Frets)
    {
        public float Beats { get; set; }
        public float RestBefore { get; set; }
    }

    public static TabPdfGuide.Document Build(string pdfSha256, int page, LessonTabStaff staff, int[] tuning,
        int capo, int bpm, string coverage, IEnumerable<Column> columns)
    {
        if (bpm is < 20 or > 300)
            throw new InvalidDataException("Enter a tempo between 20 and 300 BPM.");
        if (string.IsNullOrWhiteSpace(coverage) || coverage.Length > 150)
            throw new InvalidDataException("Enter a short label for this passage.");
        var timeline = new List<TabPdfGuide.Attack>();
        var beat = 0d;
        foreach (var column in columns)
        {
            if (column.Beats is not (>= .0625f and <= 32) || column.RestBefore is not (>= 0 and <= 32))
                throw new InvalidDataException($"Set the note length and rests for column {timeline.Count + 1}.");
            var start = (beat += column.RestBefore) * (60d / bpm);
            var end = (beat += column.Beats) * (60d / bpm);
            var frets = Frets(column, end - start);
            timeline.Add(new(start, end, column.X, column.Width, 0, frets));
        }
        var document = new TabPdfGuide.Document(1, pdfSha256, false, coverage.Trim(), (int[])tuning.Clone(), capo,
            [new(page, staff.Left, staff.Top, staff.Right, staff.Bottom, timeline.ToArray())]);
        _ = TabPdfGuide.FromDocument(document);
        return document;
    }

    private static TabPdfGuide.Fret[] Frets(Column column, double duration)
    {
        if (column.Frets is not { Length: 6 } || column.Frets.Any(fret => fret is < -1 or > 24) ||
            column.Frets.All(fret => fret < 0))
            throw new InvalidDataException("Every selected column needs at least one fret from 0 to 24; use -1 for silent strings.");
        return column.Frets.Select((fret, index) => (fret, index))
            .Where(entry => entry.fret >= 0)
            .Select(entry => new TabPdfGuide.Fret(entry.index + 1, entry.fret, duration)).ToArray();
    }

    public static TabSynthNote[] Audition(Column column, int[] tuning, int capo)
    {
        if (capo is < 0 or > 12 || tuning is not { Length: 6 } || tuning.Any(pitch => pitch is < 24 or > 84))
            throw new InvalidDataException("Set six tuning pitches in the range 24–84 and a capo from 0 to 12.");
        return Frets(column, .45).Select(fret => new TabSynthNote(0, fret.Hold,
            tuning[6 - fret.String] + capo + fret.Number, 82)).ToArray();
    }
}
