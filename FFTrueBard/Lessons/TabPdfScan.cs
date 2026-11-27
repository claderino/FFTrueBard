namespace FFTrueBard.Lessons;

internal static class TabPdfScan
{
    internal sealed record Page(int Number, int PageCount, string Hash, TabPdfDraft.Detection Detection,
        TabPdfRhythm.Reading[] Rhythms, TabPdfRhythm.Settings Settings, bool UnresolvedNavigation);
    internal sealed record Score(Page[] Pages)
    {
        public int StaffCount => Pages.Sum(p => p.Detection.Staves.Length);
        public int ColumnCount => Pages.SelectMany(p => p.Detection.Staves).Sum(row => row.Columns.Length);
        public int TimedColumns => Pages.SelectMany(p => p.Rhythms).Sum(row => row.Beats.Count(beat => beat is not null));
        public bool StandardNotationOnly => Pages.All(p => p.Detection.SixLineRows == 0) &&
            Pages.Any(p => p.Detection.FiveLineRows >= 2);

        public string? PreviewProblem
        {
            get
            {
                if (StandardNotationOnly)
                    return "This PDF appears to use standard notation, not six-string TAB. Guitar fingering would need an arrangement.";
                if (Pages.Any(p => p.Detection.SixLineRows == 0 || p.Detection.SixLineRows != p.Detection.Staves.Length))
                    return "At least one page has no readable TAB or has unreadable staff rows.";
                if (TimedColumns != ColumnCount)
                    return "Some note lengths could not be read.";
                if (Pages.Any(p => p.UnresolvedNavigation || p.Rhythms.Any(row => row.HasUnsupportedMarks)))
                    return "The score uses repeats, mixed voices, or other notation that needs review.";
                if (Pages[0].Settings is not { Bpm: not null, Capo: not null, Tuning: not null })
                    return "The opening page doesn't declare a readable tempo, tuning, and capo.";
                var first = Pages[0].Settings;
                if (Pages.Skip(1).Any(p => (p.Settings.Bpm is not null && p.Settings.Bpm != first.Bpm) ||
                    (p.Settings.Capo is not null && p.Settings.Capo != first.Capo) ||
                    (p.Settings.Tuning is not null && p.Settings.Tuning != first.Tuning)))
                    return "Tempo, tuning, or capo changes must be checked before a whole-score preview.";
                return null;
            }
        }

        public TabPdfGuide BuildPreview()
        {
            if (PreviewProblem is { } problem) throw new InvalidDataException(problem);
            const string coverage = "Unreviewed whole-score preview";
            var first = Pages[0];
            var settings = first.Settings;
            var tuning = settings.Tuning!.Split(' ').Select(int.Parse).ToArray();
            var rows = new List<TabPdfGuide.Row>();
            var time = 0d;
            foreach (var page in Pages)
            for (var i = 0; i < page.Detection.Staves.Length; i++)
            {
                var staff = page.Detection.Staves[i];
                var rhythm = page.Rhythms[i];
                var columns = staff.Columns.Select((column, index) => new TabGuideBuilder.Column(column.X, column.Width, column.Frets)
                {
                    Beats = rhythm.Beats[index]!.Value,
                    RestBefore = rhythm.RestsBefore[index],
                });
                var row = TabGuideBuilder.Build(first.Hash, page.Number, staff.Staff, tuning, settings.Capo!.Value,
                    settings.Bpm!.Value, coverage, columns).Rows[0];
                rows.Add(row with
                {
                    Attacks = row.Attacks.Select(attack => attack with { Start = attack.Start + time, End = attack.End + time }).ToArray(),
                });
                time += row.Attacks[^1].End;
            }
            return TabPdfGuide.FromDocument(new(1, first.Hash, false, coverage, tuning, settings.Capo!.Value, rows.ToArray()));
        }
    }

    public static Page ReadPage(string pluginDirectory, string pdf, int page)
    {
        var image = LessonPdfRenderer.Render(pluginDirectory, pdf, page, null, readTabDigits: true);
        var detection = TabPdfDraft.Scan(image);
        return new(page, image.PageCount, image.PdfSha256!, detection,
            detection.Staves.Select(staff => TabPdfRhythm.Read(image, staff)).ToArray(),
            TabPdfRhythm.ReadSettings(image, detection.Staves.FirstOrDefault()?.Staff.Top ?? detection.FirstTabStaff?.Top ?? 1,
                detection.TabRows),
            TabPdfRhythm.HasUnresolvedNavigation(image));
    }

    public static Score ReadScore(string pluginDirectory, string pdf)
    {
        var first = ReadPage(pluginDirectory, pdf, 1);
        if (first.PageCount > 60) throw new InvalidDataException("Whole-score detection is limited to 60 pages.");
        var pages = new List<Page>(first.PageCount) { first };
        for (var i = 2; i <= first.PageCount; i++)
        {
            var page = ReadPage(pluginDirectory, pdf, i);
            if (page.Hash != first.Hash || page.PageCount != first.PageCount)
                throw new InvalidDataException("The PDF changed during detection. Scan it again.");
            pages.Add(page);
        }
        return new(pages.ToArray());
    }
}
