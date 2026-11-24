namespace FFTrueBard.Lessons;

internal readonly record struct LessonTabStaff(double Left, double Top, double Right, double Bottom);

/// <summary>Invertible mapping between the visible TAB row and one exercise's audio range.</summary>
internal readonly struct LessonScoreCursor
{
    private readonly double left;
    private readonly double right;
    private readonly double start;
    private readonly double end;
    private readonly LessonScoreMark[]? marks;

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
        var time = start;
        foreach (var note in map.Notes)
        {
            var target = Center(note);
            // The supported book has one TAB staff per exercise. Do not attach
            // a reordered or unrelated set of marks to that staff.
            if (target <= x || target >= staff.Right || note.Start <= time || note.Start >= end ||
                note.Y + note.Height < staff.Top - .003 || note.Y > staff.Bottom)
                return false;
            x = target;
            time = note.Start;
        }
        return true;
    }

    public double XAt(double seconds) => Map(seconds, start, end, left, right, false);
    public double TimeAt(double x) => Map(x, left, right, start, end, true);

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
