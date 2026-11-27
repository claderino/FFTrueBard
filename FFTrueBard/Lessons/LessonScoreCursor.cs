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
    private readonly bool hideGaps;
    public bool NoteSynced => marks is not null;
    public double Left => left;
    public double Right => right;
    public double MusicStart => marks?[0].Start ?? start;
    public double MusicEnd => marks?[^1].End ?? end;

    public LessonScoreCursor(LessonTabStaff staff, double start, double end, LessonScorePlayhead? map, bool hideGaps = false)
    {
        left = staff.Left + (staff.Right - staff.Left) * .06; // Leave space for the TAB label.
        right = staff.Right;
        this.start = start;
        this.end = end;
        marks = MatchesStaff(staff, map, start, end) ? map!.Notes : null;
        this.hideGaps = hideGaps || (marks is not null && map!.SchemaVersion == 2);
    }

    private static double Center(LessonScoreMark mark) => mark.X + mark.Width / 2;
    public static bool MatchesStaff(LessonTabStaff staff, LessonScorePlayhead? map, double start, double end)
    {
        if (map is null || map.Notes.Length == 0 || map.SchemaVersion is not (1 or 2)) return false;
        var left = staff.Left + (staff.Right - staff.Left) * .06;
        var x = left;
        var time = double.NegativeInfinity;
        var pass = 0;
        var previousEnd = start;
        for (var i = 0; i < map.Notes.Length; i++)
        {
            var note = map.Notes[i];
            var target = Center(note);
            if (note.Pass != pass)
            {
                if (map.SchemaVersion != 2 || i == 0 || note.Pass != pass + 1) return false;
                pass = note.Pass;
                x = left;
            }
            if (target <= x || target >= staff.Right || note.Start < start || note.Start <= time || note.Start >= end ||
                note.End > end || note.Y + note.Height < staff.Top - .003 || note.Y > staff.Bottom ||
                (map.SchemaVersion == 2 && note.Start < previousEnd))
                return false;
            x = target;
            time = note.Start;
            previousEnd = note.End;
        }
        return map.SchemaVersion == 1 || pass > 0;
    }

    public bool ShowsNoteAt(double seconds)
    {
        if (marks is not { } music || seconds < MusicStart || seconds >= MusicEnd) return false;
        if (!hideGaps) return true;
        return seconds < music[MarkAtOrBefore(music, seconds)].End;
    }

    public double XAt(double seconds)
    {
        if (marks is not { } music)
            return Interpolate(Math.Clamp(seconds, start, end), start, end, left, right);
        var i = MarkAtOrBefore(music, seconds);
        var here = music[i];
        if (i + 1 == music.Length || here.Pass != music[i + 1].Pass || seconds <= here.Start)
            return Center(here);
        var next = music[i + 1];
        return Interpolate(seconds, here.Start, next.Start, Center(here), Center(next));
    }

    public double TimeAt(double x, double? nearSeconds = null)
    {
        if (marks is not { } music) return Interpolate(Math.Clamp(x, left, right), left, right, start, end);
        var (first, last) = BestPass(music, x, nearSeconds ?? MusicStart);
        return SeekInPass(music, first, last, x);
    }

    public LessonScoreMark? NoteAtX(double x, double nearSeconds)
    {
        if (marks is not { } music) return null;
        var (first, last) = BestPass(music, x, nearSeconds);
        var nearest = music[first];
        for (var i = first; i <= last; i++)
        {
            var note = music[i];
            if (x >= note.X && x <= note.X + note.Width) return note;
            if (Math.Abs(Center(note) - x) < Math.Abs(Center(nearest) - x)) nearest = note;
        }
        return nearest;
    }

    public LessonScoreMark? LoopPoint(double? seconds, bool atEnd) =>
        seconds is { } time && marks is { } music
            ? Array.Find(music, note => (atEnd ? note.End : note.Start) == time)
            : null;

    public IEnumerable<(double Left, double Right)> LoopSpans(LessonScoreMark a, LessonScoreMark b)
    {
        if (marks is not { } music || b.End - a.Start < .1) yield break;
        var pass = -1;
        var first = 0d;
        var last = 0d;
        foreach (var note in music)
        {
            if (note.Start < a.Start) continue;
            if (note.Start > b.Start) break;
            // On a repeat the next note can be farther left on the printed staff.
            // Shade each pass separately, never across the jump.
            if (note.Pass != pass)
            {
                if (pass >= 0) yield return (first, last);
                pass = note.Pass;
                first = note.X;
                last = note.X + note.Width;
            }
            else
            {
                first = Math.Min(first, note.X);
                last = Math.Max(last, note.X + note.Width);
            }
        }
        if (pass >= 0) yield return (first, last);
    }

    private static (int First, int Last) BestPass(LessonScoreMark[] music, double x, double reference)
    {
        var best = (0, 0);
        var bestXDistance = double.PositiveInfinity;
        var bestTimeDistance = double.PositiveInfinity;
        for (var first = 0; first < music.Length;)
        {
            var last = first;
            while (last + 1 < music.Length && music[last + 1].Pass == music[first].Pass) last++;
            var firstMark = music[first];
            var lastMark = music[last];
            var xDistance = Math.Max(0, Math.Max(firstMark.X - x, x - lastMark.X - lastMark.Width));
            // For the same printed fret, prefer the pass the listener is in,
            // then whichever pass is nearest to the current playback time.
            var timeDistance = Math.Max(0, Math.Max(firstMark.Start - reference, reference - lastMark.End));
            if (xDistance < bestXDistance || (xDistance == bestXDistance && timeDistance < bestTimeDistance))
            {
                bestXDistance = xDistance;
                bestTimeDistance = timeDistance;
                best = (first, last);
            }
            first = last + 1;
        }
        return best;
    }

    private static double SeekInPass(LessonScoreMark[] music, int first, int last, double x)
    {
        for (var i = first; i <= last; i++)
            if (x >= music[i].X && x <= music[i].X + music[i].Width) return music[i].Start;
        if (x <= Center(music[first])) return music[first].Start;
        for (var i = first + 1; i <= last; i++)
        {
            var next = Center(music[i]);
            if (x <= next) return Interpolate(x, Center(music[i - 1]), next, music[i - 1].Start, music[i].Start);
        }
        return music[last].Start;
    }

    private static int MarkAtOrBefore(LessonScoreMark[] music, double seconds)
    {
        var left = 0;
        var right = music.Length - 1;
        while (left < right)
        {
            var middle = (left + right + 1) / 2;
            if (music[middle].Start <= seconds) left = middle;
            else right = middle - 1;
        }
        return left;
    }

    private static double Interpolate(double value, double from, double to, double first, double last) =>
        to > from ? first + (last - first) * ((value - from) / (to - from)) : first;
}
