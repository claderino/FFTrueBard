namespace FFTrueBard.Lessons;

internal readonly record struct TabPdfDigit(int Number, double Left, double Top, double Right, double Bottom, bool Legacy = false);
internal sealed record TabDraftColumn(double X, double Width, int[] Frets);
internal sealed record TabDraftStaff(LessonTabStaff Staff, TabDraftColumn[] Columns, LessonTabStaff? NotationStaff = null,
    bool OutlinedFrets = false);

/// <summary>Finds likely six-line systems and editable fret candidates. Does not infer timing.</summary>
internal static class TabPdfDraft
{
    internal sealed record Detection(TabDraftStaff[] Staves, int SixLineRows, int FiveLineRows, LessonTabStaff[]? TabRows = null)
    {
        public LessonTabStaff? FirstTabStaff => TabRows is [var first, ..] ? first : null;
    }
    private record struct Line(int Y, int Left, int Right, int Ink);
    private record struct Fret(int String, int Number, double Left, double Right)
    {
        public double Center => (Left + Right) / 2;
    }

    public static TabDraftStaff[] Detect(LessonPdfRenderer.Image image) => Scan(image).Staves;

    internal static bool IsLegacyGuitarFont(ReadOnlySpan<byte> name)
    {
        // PDF producers can prefix an embedded subset with six letters and a plus.
        if (name.Length > 7 && name[6] == '+') name = name[7..];
        return name.SequenceEqual("Guitar Pro 5"u8) || name.SequenceEqual("GuitarPro5"u8) ||
            name.SequenceEqual("Guitar#20Pro#205"u8);
    }

    internal static int DecodeDigit(char code, bool legacy) => legacy
        ? code is >= '!' and <= '*' ? code - '!' : -1
        : char.IsAsciiDigit(code) ? code - '0' : -1;

    public static Detection Scan(LessonPdfRenderer.Image image)
    {
        if (image.TabDigits is not { } digits) return new([], 0, 0);
        var width = image.Width;
        var height = image.Height;
        var lines = new List<Line>();
        for (var y = 0; y < height; y++)
        {
            var ink = 0;
            var left = width;
            var right = 0;
            for (var x = 0; x < width; x++)
            {
                if (image.Pixels[(y * width + x) * 4 + 1] >= 180) continue;
                ink++;
                left = Math.Min(left, x);
                right = x;
            }
            if (ink < width * .32 || right - left < width * .38) continue;
            var line = new Line(y, left, right, ink);
            if (lines.Count > 0 && y - lines[^1].Y < 3)
            {
                if (ink > lines[^1].Ink) lines[^1] = line;
            }
            else lines.Add(line);
        }

        var raster = FindRows(lines);
        var vector = FindRows(VectorLines(image));
        var selected = vector.Staves.Length > raster.Staves.Length ||
            (vector.Staves.Length == raster.Staves.Length && vector.FiveLineRows > raster.FiveLineRows)
            ? vector : raster;
        return selected with { TabRows = (vector.TabRows ?? []).Concat(raster.TabRows ?? []).OrderBy(row => row.Top)
            .DistinctBy(row => Math.Round(row.Top * height / 3d)).ToArray() };

        Detection FindRows(List<Line> rows)
        {
            var result = new List<TabDraftStaff>();
            var tabRows = new List<LessonTabStaff>();
            var fiveLineRows = 0;
            LessonTabStaff? previousNotation = null;
            for (var i = 0; i + 4 < rows.Count; i++)
            {
                var run = rows.GetRange(i, 5);
                var gap = (run[^1].Y - run[0].Y) / 4d;
                if (!Regular(run, gap)) continue;
                var six = i + 5 < rows.Count && Math.Abs(rows[i + 5].Y - run[0].Y - 5 * gap) <= Math.Max(2, gap * .18);
                if (six)
                {
                    run.Add(rows[i + 5]);
                    gap = (run[^1].Y - run[0].Y) / 5d;
                    if (!Regular(run, gap)) continue;
                }
                var left = (double)run.Max(line => line.Left) / width;
                var right = (double)run.Min(line => line.Right) / width;
                if (right - left < .35) continue;
                i += run.Count - 1;
                var staff = new LessonTabStaff(left, (double)run[0].Y / height, right, (double)run[^1].Y / height);
                if (!six)
                {
                    fiveLineRows++;
                    previousNotation = staff;
                    continue;
                }
                tabRows.Add(staff);
                LessonTabStaff? notation = previousNotation is { } above && staff.Top - above.Bottom <= 9 * gap / height &&
                    Math.Abs(staff.Left - above.Left) < .05 && Math.Abs(staff.Right - above.Right) < .05 ? above : null;
                previousNotation = null;
                var columns = ReadColumns(digits, staff, gap, width, height);
                var outlined = false;
                if (columns.Length == 0 && notation is null)
                {
                    var shapes = ReadOutlinedDigits(image, staff);
                    columns = ReadColumns(shapes, staff, gap, width, height);
                    outlined = columns.Length > 0;
                }
                if (columns.Length == 0) continue;
                result.Add(new(staff, columns, notation, outlined));
            }
            return new(result.ToArray(), tabRows.Count, fiveLineRows, tabRows.ToArray());
        }

        bool Regular(List<Line> run, double gap) => gap >= 5 && gap <= height * .06 &&
            run.Select((line, index) => Math.Abs(line.Y - run[0].Y - index * gap)).All(error => error <= Math.Max(2, gap * .18));
    }

    private static TabPdfDigit[] ReadOutlinedDigits(LessonPdfRenderer.Image image, LessonTabStaff staff)
    {
        if (image.TabNotation?.Contours is not { Length: > 0 } contours) return [];
        var dy = (staff.Bottom - staff.Top) / 5;
        var dx = dy * image.Height / image.Width;
        var digits = new List<TabPdfDigit>();
        foreach (var shape in contours)
        {
            var stringLine = (shape.Top + shape.Bottom - 2 * staff.Top) / (2 * dy);
            if (shape.Left <= staff.Left || shape.Right >= staff.Right || stringLine is < -.25 or > 5.25 ||
                Math.Abs(stringLine - Math.Round(stringLine)) > .25 ||
                (shape.Right - shape.Left) / dx is < .49 or > .68 || (shape.Bottom - shape.Top) / dy is < .79 or > .93)
                continue;
            // Outlined sans-serif digits found in rhythm-under-TAB exports. Require
            // the contour topology, proportions, and position; never coerce other marks.
            var digit = (shape.Points, shape.CurvePoints) switch
            {
                (12, 0) => 4,
                (>= 39 and <= 41, 36) => 3,
                (>= 25 and <= 27, 24) => 6,
                (12 or 13, 6) => 7,
                _ => -1,
            };
            if (digit >= 0) digits.Add(new(digit, shape.Left, shape.Top, shape.Right, shape.Bottom));
        }
        return digits.Count >= 4 && digits.Select(d => d.Number).Distinct().Take(2).Count() == 2 ? digits.ToArray() : [];
    }

    private static List<Line> VectorLines(LessonPdfRenderer.Image image)
    {
        if (image.TabNotation is not { } notation) return [];
        var width = image.Width;
        var height = image.Height;
        var strokes = notation.Strokes.Where(s => !s.Filled)
            .Select(s => (A: s.Points[0], B: s.Points[1]))
            .Where(p => Math.Abs(p.A.Y - p.B.Y) * height < .7 && Math.Abs(p.A.X - p.B.X) > .03)
            .Select(p => new Line((int)Math.Round((p.A.Y + p.B.Y) * .5 * height),
                (int)Math.Round(Math.Min(p.A.X, p.B.X) * width),
                (int)Math.Round(Math.Max(p.A.X, p.B.X) * width), 0));
        var result = new List<Line>();
        foreach (var row in strokes.GroupBy(l => l.Y).OrderBy(g => g.Key))
        {
            var right = 0;
            var left = 0;
            foreach (var piece in row.OrderBy(l => l.Left))
            {
                if (piece.Left > right + width * .012 || right == 0)
                {
                    if (right - left > width * .38) result.Add(new(row.Key, left, right, right - left));
                    left = piece.Left;
                }
                right = Math.Max(piece.Right, right);
            }
            if (right - left > width * .38) result.Add(new(row.Key, left, right, right - left));
        }
        // Ignore multiple candidate ranges on the same baseline; they must not be interpreted as separate strings.
        return result.GroupBy(l => l.Y).Select(row => row.MaxBy(l => l.Ink)).ToList();
    }

    private static TabDraftColumn[] ReadColumns(TabPdfDigit[] digits, LessonTabStaff staff, double gap, int width, int height)
    {
        var left = staff.Left + (staff.Right - staff.Left) * .06;
        var step = gap / width;
        var frets = new List<Fret>();
        for (var s = 0; s < 6; s++)
        {
            var lineY = staff.Top + s * gap / height;
            var row = digits.Where(d => d.Left >= (d.Legacy ? staff.Left : left) && d.Right <= staff.Right &&
                Math.Abs((d.Top + d.Bottom) * .5 - lineY) <= .44 * gap / height &&
                (d.Bottom - d.Top) * height <= 1.4 * gap)
                .OrderBy(d => d.Left).ToArray();
            for (var j = 0; j < row.Length;)
            {
                var first = row[j++];
                var last = first;
                var value = first.Number;
                var size = 1;
                while (j < row.Length && row[j].Legacy == last.Legacy &&
                    row[j].Left - last.Right <= (last.Legacy ? .43 : .2) * step)
                {
                    last = row[j++];
                    if (++size <= 2) value = value * 10 + last.Number;
                }
                // Don't present an ambiguous run as a guessed note.
                if (size <= 2 && value <= 24) frets.Add(new(s, value, first.Left, last.Right));
            }
        }

        var sorted = frets.OrderBy(f => f.Center).ToArray();
        var columns = new List<TabDraftColumn>();
        for (var i = 0; i < sorted.Length;)
        {
            var first = sorted[i++];
            var min = first.Left;
            var max = first.Right;
            var values = new int[6];
            Array.Fill(values, -1);
            values[first.String] = first.Number;
            while (i < sorted.Length && sorted[i].Center - first.Center <= .37 * step && values[sorted[i].String] < 0)
            {
                var other = sorted[i++];
                values[other.String] = other.Number;
                min = Math.Min(min, other.Left);
                max = Math.Max(max, other.Right);
            }
            columns.Add(new(min, max - min, values));
        }
        return columns.Count is >= 2 and <= 256 ? columns.ToArray() : [];
    }
}
