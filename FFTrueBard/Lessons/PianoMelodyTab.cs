using System.Text;

namespace FFTrueBard.Lessons;

/// <summary>Reads a supported engraved piano opening, then maps its top line to standard guitar tuning.</summary>
internal static class PianoMelodyTab
{
    internal sealed record Melody(TabPdfGuide.Document Document, string Text, int Measures, int Bpm);
    internal readonly record struct Fingering(int String, int Fret);
    private readonly record struct Stem(double X, double Top, double Bottom);
    private readonly record struct Note(int Pitch, int Tick, int Length, double X, double Width);
    private static readonly int[] Tuning = [40, 45, 50, 55, 59, 64];
    private static readonly int[] Scale = [0, 2, 4, 5, 7, 9, 11];

    internal static Melody ReadOpening(LessonPdfRenderer.Image image)
    {
        if (image.PdfSha256 is not { } hash || image.TabNotation is not { Contours: { Length: > 0 } shapes } notation)
            throw new InvalidDataException("This piano PDF has no readable vector notation.");
        var (staff, bassTop) = FindOpening(notation);
        var dy = (staff.Bottom - staff.Top) / 4;
        var dx = dy * image.Height / image.Width;
        double Height(TabPdfContour shape) => (shape.Bottom - shape.Top) / dy;
        double Width(TabPdfContour shape) => (shape.Right - shape.Left) / dx;
        double X(TabPdfContour shape) => (shape.Left + shape.Right) / 2;
        double Y(TabPdfContour shape) => (shape.Top + shape.Bottom) / 2;
        var header = shapes.Where(p => p.Left > staff.Left && p.Right < staff.Left + 9 * dx &&
            p.Top >= staff.Top - 1.5 * dy && p.Bottom <= staff.Bottom + 2 * dy).ToArray();
        var treble = header.Any(p => p.CurvePoints == 60 && Height(p) is >= 2.3 and <= 2.6 &&
            Width(p) is >= 1.2 and <= 1.4 && p.Left < staff.Left + 2 * dx);
        var sharp = header.Where(p => p.CurvePoints == 24 && Height(p) is >= 2.55 and <= 2.85 &&
            Width(p) is >= .85 and <= 1.1).ToArray();
        var fours = header.Where(p => p.CurvePoints == 72 && Height(p) is >= 1.88 and <= 2.1 &&
            Width(p) is >= 1.62 and <= 1.82).ToArray();
        if (!treble || sharp is not [var keySign] || Math.Abs(Y(keySign) - staff.Top) > .25 * dy || fours.Length != 2 ||
            Math.Abs(X(fours[0]) - X(fours[1])) > .12 * dx || fours.Min(p => p.Left) <= keySign.Right)
            throw new InvalidDataException("Melody conversion needs a supported treble clef, one-sharp key, and printed 4/4 meter.");
        var musicLeft = fours.Max(p => p.Right) + .3 * dx;
        var bpm = TabPdfRhythm.ReadSettings(image, staff.Top).Bpm ?? ReadOutlinedTempo();
        if (bpm is null)
            throw new InvalidDataException("The printed quarter-note tempo could not be read.");

        var vertical = notation.Strokes.Where(p => !p.Filled && Math.Abs(p.Points[0].X - p.Points[1].X) <= .08 * dx)
            .Select(p => new Stem(p.Points[0].X, p.Points.Min(v => v.Y), p.Points.Max(v => v.Y)))
            .Where(stem => stem.X > musicLeft && stem.X <= staff.Right + .2 * dx).Distinct().ToArray();
        var bars = vertical.Where(stem => Math.Abs(stem.Top - staff.Top) < .3 * dy &&
            stem.Bottom >= bassTop - .2 * dy && stem.Bottom <= bassTop + 5 * dy)
            .Select(stem => stem.X).DistinctBy(x => Math.Round(x / (.15 * dx))).Order().ToArray();
        if (bars.Length < 2 || staff.Right - bars[^1] > .5 * dx)
            throw new InvalidDataException("The piano opening has no readable measure boundaries.");
        var stems = vertical.Where(s => s.Bottom - s.Top > 1.4 * dy && s.Top >= staff.Top - 6 * dy &&
            s.Bottom <= staff.Bottom + 3 * dy && !bars.Contains(s.X)).ToArray();
        var beams = notation.Strokes.Where(p => p.Filled)
            .Select(p => p.Points.Take(4).OrderBy(v => v.X).ToArray())
            .Where(p => Math.Abs(p[0].X - p[1].X) < .12 * dx && Math.Abs(p[2].X - p[3].X) < .12 * dx &&
                Math.Abs(p[0].Y - p[1].Y) / dy is >= .39 and <= .65 &&
                Math.Abs(p[2].Y - p[3].Y) / dy is >= .39 and <= .65 &&
                p[2].X - p[1].X > .7 * dx && p.Min(v => v.Y) >= staff.Top - 6 * dy &&
                p.Max(v => v.Y) < staff.Bottom + 2.6 * dy &&
                Math.Abs((p[0].Y + p[1].Y) / 2 - (p[2].Y + p[3].Y) / 2) < (p[2].X - p[0].X) * 1.5)
            .ToArray();
        var heads = shapes.Where(p => p.CurvePoints == 24 && p.Points is 25 or 26 &&
            Width(p) is >= 1.19 and <= 1.39 && Height(p) is >= .94 and <= 1.2 &&
            p.Left >= musicLeft && p.Right <= staff.Right && Y(p) >= staff.Top - 2 * dy &&
            Y(p) < Math.Min(staff.Bottom + 1.85 * dy, bassTop - 1.7 * dy)).ToArray();
        if (heads.Length == 0)
            throw new InvalidDataException("No supported piano melody noteheads were found.");

        var measures = 0;
        var notes = new List<Note>();
        var left = musicLeft;
        foreach (var right in bars)
        {
            var events = new List<Note>();
            var tick = measures * 16;
            var endTick = tick + 16;
            var measure = heads.Where(p => X(p) > left && X(p) < right).OrderBy(X).ToArray();
            var accepted = 0;
            if (measure.Length == 0)
            {
                // A centered whole-bar rest in a measured staff, not an empty crop.
                if (!shapes.Any(p => p.CurvePoints == 12 && Width(p) is >= 1.2 and <= 1.4 &&
                    Height(p) is >= .48 and <= .60 && Math.Abs(X(p) - (left + right) / 2) < 1.5 * dx &&
                    p.Top >= staff.Top + .9 * dy && p.Bottom <= staff.Top + 1.7 * dy)) break;
                tick = endTick;
            }
            for (var i = 0; i < measure.Length;)
            {
                var first = i++;
                while (i < measure.Length && X(measure[i]) - X(measure[first]) < .25 * dx) i++;
                var chord = measure[first..i];
                var head = chord.MinBy(Y);
                // Use the top note of a simultaneous chord; a common stem can reach another head.
                var stem = stems.SelectMany(s => new[]
                {
                    (Tip: s.Top, Error: Math.Abs(s.X - head.Right) / dx + Math.Abs(s.Bottom - chord.Max(Y)) / dy),
                    (Tip: s.Bottom, Error: Math.Abs(s.X - head.Left) / dx + Math.Abs(s.Top - Y(head)) / dy),
                }.Select(v => (v.Tip, v.Error, s.X))).Where(v => v.Error < .58).OrderBy(v => v.Error).FirstOrDefault();
                if (stem == default) break;
                var layers = BeamCount(stem.X, stem.Tip, Y(head));
                var length = layers switch { 0 => 4, 1 => 2, 2 => 1, _ => 0 };
                var nextX = i < measure.Length ? measure[i].Left : right;
                if (shapes.Any(p => p.CurvePoints == 24 && p.Points is 25 or 26 &&
                    Width(p) is >= .35 and <= .45 && Height(p) is >= .35 and <= .45 &&
                    p.Left > head.Right + .15 * dx && p.Right < nextX && p.Left - head.Right < 1.5 * dx &&
                    Math.Abs(Y(p) - Y(head)) < .58 * dy))
                {
                    // The supported grid includes dotted eighths and quarters.
                    if (length % 2 != 0) break;
                    length = length * 3 / 2;
                }
                var step = (staff.Bottom - Y(head)) * 2 / dy;
                var diatonic = (int)Math.Round(step);
                if (Math.Abs(step - diatonic) > .20 || length == 0 || tick + length > endTick) break;
                // Reject chromatic marks at this note's height. We don't guess a changed pitch.
                if (shapes.Any(p => p.Left >= left && p.Right < head.Left && head.Left - p.Right < 1.25 * dx &&
                    Height(p) is >= 2.1 and <= 3.2 && Width(p) is >= .5 and <= 1.15 &&
                    Math.Abs(Y(p) - Y(head)) < .62 * dy)) break;
                // Reject connected notes (including ties) instead of adding a false attack.
                if (shapes.Any(p => p.Left < head.Left + .3 * dx && p.Right > head.Right + .6 * dx &&
                    p.CurvePoints is >= 6 and <= 15 && Width(p) > 1.9 && Height(p) is >= .14 and < 1 &&
                    Math.Abs(Y(p) - Y(head)) < 1.65 * dy)) break;
                events.Add(new(Pitch(diatonic), tick, length, head.Left, head.Right - head.Left));
                tick += length;
                accepted = i;
            }
            if (tick != endTick || accepted != measure.Length) break;
            notes.AddRange(events);
            measures++;
            left = right;
        }
        if (notes.Count < 2)
            throw new InvalidDataException("No complete melody measures could be read with reliable pitches and timing.");

        var fingerings = Finger(notes.Select(n => n.Pitch).ToArray());
        var secondsPerTick = 15d / bpm.Value;
        var attacks = notes.Select((note, i) => new TabPdfGuide.Attack(
            note.Tick * secondsPerTick, (note.Tick + note.Length) * secondsPerTick, note.X, note.Width, 0,
            [new(fingerings[i].String, fingerings[i].Fret, note.Length * secondsPerTick)])).ToArray();
        var doc = new TabPdfGuide.Document(1, hash, false, $"Piano melody · opening {measures} measures", Tuning, 0,
            [new(1, staff.Left, staff.Top - .5 * dy, staff.Right, staff.Bottom + 1.55 * dy, attacks)]);
        return new(doc, TextTab(notes, fingerings, measures, bpm.Value), measures, bpm.Value);

        int BeamCount(double x, double tip, double y)
        {
            var hits = new List<double>();
            foreach (var p in beams)
            {
                if (x < p[0].X - .12 * dx || x > p[3].X + .12 * dx) continue;
                var amount = Math.Clamp((x - p[0].X) / (p[3].X - p[0].X), 0d, 1d);
                var center = (p[0].Y + p[1].Y) * (1 - amount) / 2 + (p[2].Y + p[3].Y) * amount / 2;
                if (Math.Abs(center - tip) < 1.2 * dy && (center - tip) * Math.Sign(y - tip) >= -.46 * dy &&
                    hits.All(h => Math.Abs(h - center) > .3 * dy))
                    hits.Add(center);
            }
            return hits.Any(h => Math.Abs(h - tip) < .45 * dy) ? hits.Count : 0;
        }

        int? ReadOutlinedTempo()
        {
            var marking = shapes.Where(p => p.Left > staff.Left && p.Right < staff.Left + 20 * dx &&
                p.Top > staff.Top - 6 * dy && p.Bottom < staff.Top - 1.2 * dy).ToArray();
            // In this outlined font, a quarter glyph contains a stem and an eight-curve filled head.
            foreach (var quarter in marking.Where(p => p.Points is 28 or 29 && p.CurvePoints == 24 &&
                Width(p) is >= .98 and <= 1.12 && Height(p) is >= 2.45 and <= 2.7))
            {
                var equals = marking.Where(p => p.Points is 4 or 5 && p.CurvePoints == 0 &&
                    p.Left > quarter.Right && p.Left - quarter.Right < 1.2 * dx &&
                    Width(p) is >= 1.1 and <= 1.3 && Height(p) is >= .22 and <= .34 &&
                    Math.Abs(Y(p) - quarter.Bottom) < 1.5 * dy).ToArray();
                if (equals.Length != 2 || Math.Abs(X(equals[0]) - X(equals[1])) > .12 * dx) continue;
                var digits = marking.Where(p => p.Left > equals.Max(q => q.Right) && Width(p) is >= .78 and <= 1.45 &&
                    Height(p) is >= 1.55 and <= 1.9 && Math.Abs(p.Bottom - quarter.Bottom) < .38 * dy)
                    .OrderBy(p => p.Left).Select(p => (p.Points, p.CurvePoints) switch
                    {
                        (29 or 30, 18) => '1',
                        (75 or 76, 72) => '5',
                        _ => '?',
                    }).ToArray();
                if (digits.Length == 3 && int.TryParse(digits, out var tempo) && tempo is >= 20 and <= 300)
                    return tempo;
            }
            return null;
        }
    }

    private static (LessonTabStaff Top, double BassTop) FindOpening(TabPdfNotation notation)
    {
        var lines = notation.Strokes.Where(s => !s.Filled && Math.Abs(s.Points[0].Y - s.Points[1].Y) < .0001 &&
            Math.Abs(s.Points[0].X - s.Points[1].X) > .55)
            .Select(s => (Y: s.Points[0].Y, Left: s.Points.Min(p => p.X), Right: s.Points.Max(p => p.X)))
            .DistinctBy(s => Math.Round(s.Y, 4)).OrderBy(s => s.Y).Take(10).ToArray();
        if (lines.Length != 10) throw new InvalidDataException("No paired five-line piano staves were found.");
        var gap = (lines[4].Y - lines[0].Y) / 4;
        if (gap is < .003 or > .035 || lines[5].Y - lines[4].Y < 1.85 * gap ||
            lines[5].Y - lines[4].Y > 7 * gap || lines.Where((line, i) =>
                Math.Abs(line.Y - lines[i / 5 * 5].Y - i % 5 * gap) > .1 * gap ||
                Math.Abs(line.Left - lines[0].Left) > .008 || Math.Abs(line.Right - lines[0].Right) > .008).Any())
            throw new InvalidDataException("The opening staves don't match the supported single grand-staff layout.");
        return (new(lines[0].Left, lines[0].Y, lines[0].Right, lines[4].Y), lines[5].Y);
    }

    // The treble staff's bottom line is E4; F is sharpened by the opening key signature.
    internal static int Pitch(int step)
    {
        var position = step + 2;
        var index = ((position % 7) + 7) % 7;
        return 60 + 12 * ((position - index) / 7) + Scale[index] + (index == 3 ? 1 : 0);
    }

    internal static Fingering[] Finger(int[] pitches)
    {
        var result = new Fingering[pitches.Length];
        if (pitches.Length == 0) return result;
        var options = pitches.Select(pitch => Tuning.Reverse().Select((open, index) =>
            new Fingering(index + 1, pitch - open)).Where(fret => fret.Fret is >= 0 and <= 24).ToArray()).ToArray();
        if (options.Any(row => row.Length == 0))
            throw new InvalidDataException("A melody pitch is outside the guitar's standard-tuning range.");
        var paths = new (int Score, int Previous)[pitches.Length][];
        for (var i = 0; i < options.Length; i++)
        {
            paths[i] = new (int, int)[options[i].Length];
            for (var j = 0; j < options[i].Length; j++)
            {
                var next = options[i][j];
                var positionCost = next.Fret + 3 * Math.Max(0, next.Fret - 7);
                paths[i][j] = i == 0 ? (positionCost, -1) : paths[i - 1].Select((path, k) =>
                {
                    var previous = options[i - 1][k];
                    var travel = Math.Abs(next.String - previous.String) +
                        (next.Fret == 0 || previous.Fret == 0 ? 0 : 2 * Math.Abs(next.Fret - previous.Fret));
                    return (Score: path.Score + travel + positionCost, Previous: k);
                }).MinBy(path => path.Score);
            }
        }
        var index = Enumerable.Range(0, paths[^1].Length).MinBy(i => paths[^1][i].Score);
        for (var i = pitches.Length - 1; i >= 0; i--)
        {
            result[i] = options[i][index];
            index = paths[i][index].Previous;
        }
        return result;
    }

    private static string TextTab(List<Note> notes, Fingering[] fingers, int measures, int bpm)
    {
        var text = new StringBuilder($"Melody-only guitar TAB — opening {measures} measures\n" +
            $"Standard tuning, no capo · 4/4 · {bpm} BPM\nEach column is a sixteenth note. Digits mark attacks; ~ is a held note.\n");
        var labels = new[] { "e", "B", "G", "D", "A", "E" };
        var grid = new Fingering?[measures * 16];
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            grid[note.Tick] = fingers[i];
            for (var tick = note.Tick + 1; tick < note.Tick + note.Length; tick++)
                grid[tick] = fingers[i] with { Fret = -1 };
        }
        for (var block = 0; block < measures; block += 2)
        {
            text.AppendLine($"\nMeasures {block + 1}–{Math.Min(block + 2, measures)}");
            for (var s = 1; s <= 6; s++)
            {
                text.Append(labels[s - 1]).Append('|');
                for (var m = block; m < Math.Min(block + 2, measures); m++)
                {
                    for (var tick = m * 16; tick < (m + 1) * 16; tick++)
                        text.Append(grid[tick] is { } fret && fret.String == s
                            ? fret.Fret == -1 ? "~~" : fret.Fret.ToString().PadRight(2, '-')
                            : "--");
                    text.Append('|');
                }
                text.AppendLine();
            }
        }
        return text.ToString();
    }
}
