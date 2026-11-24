namespace FFTrueBard.Lessons;

internal readonly record struct LessonTabStaff(double Left, double Top, double Right, double Bottom);

/// <summary>Maps reviewed note positions, keeping audio outside the phrase off the score.</summary>
internal readonly struct LessonScoreCursor
{
    private readonly double left;
    private readonly double right;
    private readonly double start;
    private readonly double end;
    private readonly LessonScoreMark[]? marks;
    public bool NoteSynced => marks is not null;
    public double Left => left;
    public double Right => right;
    public double MusicStart => marks?[0].Start ?? start;
    public double MusicEnd => marks?[^1].End ?? end;

    public LessonScoreCursor(LessonTabStaff staff, double start, double end, LessonScorePlayhead? map)
    {
        left = staff.Left + (staff.Right - staff.Left) * .06; // Leave space for the TAB label.
        right = staff.Right;
        this.start = start;
        this.end = end;
        marks = MatchesStaff(staff, map, start, end) ? map!.Notes : null;
    }

    private static double Center(LessonScoreMark mark) => mark.X + mark.Width / 2;
    public static bool MatchesStaff(LessonTabStaff staff, LessonScorePlayhead? map, double start, double end)
    {
        if (map is null || map.Notes.Length == 0) return false;
        var x = staff.Left + (staff.Right - staff.Left) * .06;
        var time = double.NegativeInfinity;
        foreach (var note in map.Notes)
        {
            var target = Center(note);
            // The supported book has one TAB staff per exercise. Do not attach
            // a reordered or unrelated set of marks to that staff.
            if (target <= x || target >= staff.Right || note.Start < start || note.Start <= time || note.Start >= end ||
                note.Y + note.Height < staff.Top - .003 || note.Y > staff.Bottom)
                return false;
            x = target;
            time = note.Start;
        }
        return true;
    }

    public bool ShowsNoteAt(double seconds) => NoteSynced && seconds >= MusicStart && seconds < MusicEnd;

    public double XAt(double seconds) => marks is { } music
        ? Map(seconds, MusicStart, music[^1].Start, Center(music[0]), Center(music[^1]), false)
        : Map(seconds, start, end, left, right, false);

    public double TimeAt(double x)
    {
        if (marks is not { } music) return Map(x, left, right, start, end, true);
        foreach (var mark in music)
        {
            // Click the printed fret itself to start at its onset.
            if (x >= mark.X && x <= mark.X + mark.Width) return mark.Start;
        }
        return Map(x, Center(music[0]), Center(music[^1]), MusicStart, music[^1].Start, true);
    }

    private double Map(double value, double from, double to, double first, double last, bool toTime)
    {
        value = Math.Clamp(value, from, to);
        if (marks is not null)
        {
            foreach (var mark in marks)
            {
                var x = Center(mark);
                var anchor = toTime ? x : mark.Start;
                var mapped = toTime ? mark.Start : x;
                if (value <= anchor) return Interpolate(value, from, anchor, first, mapped);
                from = anchor;
                first = mapped;
            }
        }
        return Interpolate(value, from, to, first, last);
    }

    private static double Interpolate(double value, double from, double to, double first, double last) =>
        to > from ? first + (last - first) * ((value - from) / (to - from)) : first;
}
