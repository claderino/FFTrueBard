using System.Text;

namespace FFTrueBard.Lessons;

/// <summary>Reads a consecutive passage of supported engraved piano notation in standard guitar tuning.</summary>
internal static class PianoMelodyTab
{
    internal sealed record Melody(TabPdfGuide.Document Document, string Text, int Measures, int Bpm, string? StopReason);
    internal readonly record struct Fingering(int String, int Fret);
    private readonly record struct Stem(double X, double Top, double Bottom);
    private readonly record struct Note(int Pitch, int Tick, int Length, double X, double Width);
    private readonly record struct SystemPassage(List<Note> Notes, int Measures, int Bpm, string? StopReason);
    private static readonly int[] Tuning = [40, 45, 50, 55, 59, 64];
    private static readonly int[] Scale = [0, 2, 4, 5, 7, 9, 11];

    internal static Melody ReadOpening(LessonPdfRenderer.Image image) => Read(_ => image, openingOnly: true);

    internal static Melody ReadScore(Func<int, LessonPdfRenderer.Image> render, CancellationToken cancellation = default) =>
        Read(render, openingOnly: false, cancellation);

    private static Melody Read(Func<int, LessonPdfRenderer.Image> render, bool openingOnly, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var first = render(1);
        if (first.PdfSha256 is not { } hash)
            throw new InvalidDataException("This piano PDF has no readable vector notation.");
        var rows = new List<(int Page, LessonTabStaff Staff, List<Note> Notes)>();
        var measures = 0;
        var bpm = 0;
        string? stop = null;
        var pageLimit = openingOnly ? 1 : Math.Min(first.PageCount, 64);
        for (var page = 1; page <= pageLimit && stop is null; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = page == 1 ? first : render(page);
            if (image.PdfSha256 != hash)
                throw new InvalidDataException("The piano PDF changed while it was being read.");
            if (TabPdfRhythm.HasUnresolvedNavigation(image))
            {
                stop = $"Page {page}, before measure {measures + 1}: score navigation or a tempo instruction needs review.";
                break;
            }
            var systems = FindSystems(image.TabNotation).ToArray();
            if (systems.Length == 0)
                stop = $"Page {page}: no supported piano staff, starting at measure {measures + 1}.";
            for (var i = 0; i < systems.Length && stop is null && (!openingOnly || i == 0); i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (systems[i] is not { } system)
                {
                    stop = $"Page {page}, system {i + 1}, measure {measures + 1}: unsupported piano staff layout.";
                    break;
                }
                try
                {
                    var part = ReadSystem(image, system.Top, system.BassTop, bpm, measures);
                    bpm = part.Bpm;
                    var dy = (system.Top.Bottom - system.Top.Top) / 4;
                    if (part.Notes.Count != 0)
                        rows.Add((page, system.Top with
                        {
                            Top = Math.Max(0, system.Top.Top - 1.55 * dy),
                            Bottom = Math.Min(1, system.Top.Bottom + 2.05 * dy),
                        }, part.Notes));
                    measures += part.Measures;
                    if (part.StopReason is { } reason)
                        stop = $"Page {page}, system {i + 1}, measure {measures + 1}: {reason}";
                }
                catch (InvalidDataException e)
                {
                    stop = $"Page {page}, system {i + 1}, measure {measures + 1}: {e.Message}";
                }
            }
        }
        if (!openingOnly && stop is null && first.PageCount > pageLimit)
            stop = $"Stopped at the {pageLimit}-page limit, before measure {measures + 1}.";
        var notes = rows.SelectMany(row => row.Notes).ToList();
        if (notes.Count == 0)
            throw new InvalidDataException(stop ?? "No complete piano melody measures could be read.");

        var fingerings = Finger(notes.Select(n => n.Pitch).ToArray());
        var secondsPerTick = 15d / bpm;
        var index = 0;
        var guideRows = rows.Select(row => new TabPdfGuide.Row(row.Page, row.Staff.Left, row.Staff.Top,
            row.Staff.Right, row.Staff.Bottom, row.Notes.Select(note =>
            {
                var fingering = fingerings[index++];
                return new TabPdfGuide.Attack(note.Tick * secondsPerTick, (note.Tick + note.Length) * secondsPerTick,
                    note.X, note.Width, 0, [new(fingering.String, fingering.Fret, note.Length * secondsPerTick)]);
            }).ToArray())).ToArray();
        var complete = !openingOnly && stop is null;
        var coverage = complete ? $"Piano melody · {measures} measures" : $"Piano melody · first {measures} measures";
        var doc = new TabPdfGuide.Document(1, hash, false, coverage, Tuning, 0, guideRows,
            measures * 16 * secondsPerTick);
        return new(doc, TextTab(notes, fingerings, measures, bpm, complete, stop), measures, bpm, stop);
    }

    private static SystemPassage ReadSystem(LessonPdfRenderer.Image image, LessonTabStaff staff, double bassTop,
        int previousBpm, int previousMeasures)
    {
        if (image.TabNotation is not { Contours: { Length: > 0 } shapes } notation)
            throw new InvalidDataException("This piano PDF has no readable vector notation.");
        var opening = previousBpm == 0;
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
            p.Left < staff.Left + 6 * dx &&
            Width(p) is >= .85 and <= 1.1).ToArray();
        var fours = header.Where(p => p.CurvePoints == 72 && Height(p) is >= 1.88 and <= 2.1 &&
            Width(p) is >= 1.62 and <= 1.82).ToArray();
        if (!treble || sharp is not [var keySign] || Math.Abs(Y(keySign) - staff.Top) > .25 * dy ||
            (opening && (fours.Length != 2 || Math.Abs(X(fours[0]) - X(fours[1])) > .12 * dx ||
            fours.Min(p => p.Left) <= keySign.Right)))
            throw new InvalidDataException("Melody conversion needs a supported treble clef, one-sharp key, and printed 4/4 meter.");
        var musicLeft = (opening ? fours.Max(p => p.Right) : keySign.Right) + .3 * dx;
        if (!opening && (fours.Any(p => p.Left > keySign.Right && p.Left - keySign.Right < 2 * dx) ||
            header.Any(p => p.Left > keySign.Right && p.Left - keySign.Right < dx &&
                p.CurvePoints == 24 && Height(p) is >= 2.55 and <= 2.85 && Width(p) is >= .85 and <= 1.1)))
            throw new InvalidDataException("A change of key or meter needs review.");
        var printedTempo = ReadOutlinedTempo();
        var bpm = opening ? TabPdfRhythm.ReadSettings(image, staff.Top).Bpm ?? printedTempo ?? 0 : previousBpm;
        if (bpm == 0)
            throw new InvalidDataException("The printed quarter-note tempo could not be read.");
        if (!opening && printedTempo is { } nextTempo && nextTempo != bpm)
            throw new InvalidDataException("A change of tempo needs review.");

        var vertical = notation.Strokes.Where(p => !p.Filled && Math.Abs(p.Points[0].X - p.Points[1].X) <= .08 * dx)
            .Select(p => new Stem(p.Points[0].X, p.Points.Min(v => v.Y), p.Points.Max(v => v.Y)))
            .Where(stem => stem.X > musicLeft && stem.X <= staff.Right + .2 * dx).Distinct().ToArray();
        var bars = vertical.Where(stem => Math.Abs(stem.Top - staff.Top) < .3 * dy &&
            stem.Bottom >= bassTop - .2 * dy && stem.Bottom <= bassTop + 5 * dy)
            .Select(stem => stem.X).DistinctBy(x => Math.Round(x / (.15 * dx))).Order().ToArray();
        if (bars.Length == 0 || staff.Right - bars[^1] > .5 * dx)
            throw new InvalidDataException("The piano system has no readable measure boundaries.");
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
            p.Left >= musicLeft && p.Right <= staff.Right && Y(p) >= staff.Top - 3 * dy &&
            Y(p) < Math.Min(staff.Bottom + 1.85 * dy, bassTop - 1.7 * dy)).ToArray();
        var rests = shapes.Where(p => p.Left >= musicLeft && p.Right <= staff.Right &&
            Math.Abs(Y(p) - (staff.Top + 2 * dy)) < .75 * dy).Select(p => (X: X(p), Length: p.CurvePoints switch
            {
                36 when Width(p) is >= 1.05 and <= 1.17 && Height(p) is >= 1.77 and <= 1.92 => 2,
                69 when Width(p) is >= .90 and <= 1.01 && Height(p) is >= 2.88 and <= 3.06 => 4,
                _ => 0,
            })).Where(p => p.Length != 0).OrderBy(p => p.X).ToArray();
        var accidentals = shapes.Where(p => p.Left >= musicLeft && p.Top > staff.Top - 5 * dy &&
            p.Bottom < bassTop - .25 * dy).Select(p => (Shape: p, Alteration: p.CurvePoints switch
            {
                27 when Width(p) is >= .76 and <= .86 && Height(p) is >= 2.44 and <= 2.59 => -1,
                12 when Width(p) is >= .63 and <= .74 && Height(p) is >= 2.53 and <= 2.66 => 0,
                24 when Width(p) is >= .91 and <= 1.05 && Height(p) is >= 2.60 and <= 2.74 => 1,
                _ => 2,
            })).Where(p => p.Alteration != 2).OrderBy(p => p.Shape.Left).ToArray();
        var measures = 0;
        var notes = new List<Note>();
        var left = musicLeft;
        string? stop = null;
        foreach (var right in bars)
        {
            if ((previousMeasures + measures + 1) * 240d / bpm > 600)
            {
                stop = "the guide reached its ten-minute playback limit.";
                break;
            }
            if (right - left < 1.6 * dx)
            {
                stop = "a double barline or repeat boundary needs review.";
                break;
            }
            var events = new List<Note>();
            var tick = (previousMeasures + measures) * 16;
            var endTick = tick + 16;
            var measure = heads.Where(p => X(p) > left && X(p) < right).OrderBy(X).ToArray();
            var measureRests = rests.Where(r => r.X > left && r.X < right).ToArray();
            var measureAccidentals = accidentals.Where(a => a.Shape.Left >= left && a.Shape.Right < right).ToArray();
            var alterations = new Dictionary<int, int>();
            var assigned = new HashSet<TabPdfContour>();
            var rest = 0;
            var accepted = 0;
            if (measure.Length == 0)
            {
                // A centered whole-bar rest in a measured staff, not an empty crop.
                if (measureRests.Length != 0 || measureAccidentals.Length != 0 ||
                    !shapes.Any(p => p.CurvePoints == 12 && Width(p) is >= 1.2 and <= 1.4 &&
                    Height(p) is >= .48 and <= .60 && Math.Abs(X(p) - (left + right) / 2) < 1.5 * dx &&
                    p.Top >= staff.Top + .9 * dy && p.Bottom <= staff.Top + 1.7 * dy))
                {
                    stop = "no supported notes or whole-bar rest.";
                    break;
                }
                tick = endTick;
            }
            for (var i = 0; i < measure.Length;)
            {
                var first = i++;
                while (i < measure.Length && X(measure[i]) - X(measure[first]) < .25 * dx) i++;
                if (i < measure.Length && X(measure[i]) - X(measure[first]) is var offset &&
                    offset >= 1.05 * dx && offset <= 1.35 * dx &&
                    measure[first..i].Any(p => Math.Abs(Math.Abs(Y(p) - Y(measure[i])) - .5 * dy) < .12 * dy))
                {
                    var second = i++;
                    while (i < measure.Length && X(measure[i]) - X(measure[second]) < .25 * dx) i++;
                }
                var chord = measure[first..i];
                var head = chord.MinBy(Y);
                while (rest < measureRests.Length && measureRests[rest].X < X(measure[first]))
                    tick += measureRests[rest++].Length;
                // Use the top note of a simultaneous chord; a common stem can reach another head.
                var stem = stems.SelectMany(s => new[]
                {
                    (Tip: s.Top, Error: chord.Min(p => Math.Abs(s.X - p.Right)) / dx + Math.Abs(s.Bottom - chord.Max(Y)) / dy),
                    (Tip: s.Bottom, Error: chord.Min(p => Math.Abs(s.X - p.Left)) / dx + Math.Abs(s.Top - Y(head)) / dy),
                }.Select(v => (v.Tip, v.Error, s.X))).Where(v => v.Error < .58).OrderBy(v => v.Error).FirstOrDefault();
                if (stem == default) { stop = "a note stem could not be matched."; break; }
                var layers = BeamCount(stem.X, stem.Tip, Y(head));
                var length = layers switch { 0 => 4, 1 => 2, 2 => 1, _ => 0 };
                var nextX = i < measure.Length ? measure[i].Left : right;
                var chordRight = chord.Max(q => q.Right);
                if (shapes.Any(p => p.CurvePoints == 24 && p.Points is 25 or 26 &&
                    Width(p) is >= .35 and <= .45 && Height(p) is >= .35 and <= .45 &&
                    p.Left > chordRight + .15 * dx && p.Right < nextX && p.Left - chordRight < 1.5 * dx &&
                    chord.Any(member => Math.Abs(Y(p) - Y(member)) < .58 * dy)))
                {
                    // The supported grid includes dotted eighths and quarters.
                    if (length % 2 != 0) { stop = "a dot could not be interpreted."; break; }
                    length = length * 3 / 2;
                }
                var step = (staff.Bottom - Y(head)) * 2 / dy;
                var diatonic = (int)Math.Round(step);
                if (Math.Abs(step - diatonic) > .20 || length == 0 || tick + length > endTick)
                { stop = "pitch or rhythm does not fit the supported grid."; break; }
                foreach (var accidental in measureAccidentals.Where(a => a.Shape.Right < chord[0].Left &&
                    chord[0].Left - a.Shape.Right < 2 * dx && !assigned.Contains(a.Shape)))
                {
                    var accidentalY = accidental.Alteration == -1 ? accidental.Shape.Bottom - .70 * dy : Y(accidental.Shape);
                    var target = chord.MinBy(p => Math.Abs(Y(p) - accidentalY));
                    if (Math.Abs(Y(target) - accidentalY) > .23 * dy) continue;
                    var position = (int)Math.Round((staff.Bottom - Y(target)) * 2 / dy);
                    alterations[position] = accidental.Alteration;
                    assigned.Add(accidental.Shape);
                }
                if (shapes.Any(p => !assigned.Contains(p) && p.Left >= left && p.Right < head.Left &&
                    head.Left - p.Right < 1.25 * dx &&
                    Height(p) is >= 2.1 and <= 3.2 && Width(p) is >= .5 and <= 1.15 &&
                    Math.Abs(Y(p) - Y(head)) < .62 * dy && p.CurvePoints is 12 or 24 or 27))
                { stop = "an accidental needs review."; break; }
                // Reject connected notes (including ties) instead of adding a false attack.
                if (shapes.Any(p => p.Left < head.Left + .3 * dx && p.Right > head.Right + .6 * dx &&
                    p.CurvePoints is >= 6 and <= 15 && Width(p) > 1.9 && Height(p) is >= .14 and < 1 &&
                    Math.Abs(Y(p) - Y(head)) < 1.65 * dy))
                { stop = "a tie or slur needs review."; break; }
                var pitch = Pitch(diatonic, alterations.TryGetValue(diatonic, out var alteration) ? alteration : null);
                if (pitch < Tuning[0] || pitch > Tuning[^1] + 24)
                { stop = "a melody pitch is outside standard guitar tuning."; break; }
                events.Add(new(pitch, tick, length, head.Left, head.Right - head.Left));
                tick += length;
                accepted = i;
            }
            while (rest < measureRests.Length) tick += measureRests[rest++].Length;
            if (tick != endTick || accepted != measure.Length || assigned.Count != measureAccidentals.Length)
            {
                stop ??= "the note and rest lengths do not fill the measure.";
                break;
            }
            notes.AddRange(events);
            measures++;
            left = right;
        }
        return new(notes, measures, bpm, stop);

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

    private static IEnumerable<(LessonTabStaff Top, double BassTop)?> FindSystems(TabPdfNotation? notation)
    {
        if (notation is null) yield break;
        var lines = notation.Strokes.Where(s => !s.Filled && Math.Abs(s.Points[0].Y - s.Points[1].Y) < .0001 &&
            Math.Abs(s.Points[0].X - s.Points[1].X) > .55)
            .Select(s => (Y: s.Points[0].Y, Left: s.Points.Min(p => p.X), Right: s.Points.Max(p => p.X)))
            .DistinctBy(s => Math.Round(s.Y, 4)).OrderBy(s => s.Y).ToArray();
        foreach (var group in lines.Chunk(10))
        {
            if (group.Length != 10) { yield return null; yield break; }
            var gap = (group[4].Y - group[0].Y) / 4;
            if (gap is < .003 or > .035 || group[5].Y - group[4].Y < 1.85 * gap ||
                group[5].Y - group[4].Y > 9 * gap || group.Where((line, i) =>
                    Math.Abs(line.Y - group[i / 5 * 5].Y - i % 5 * gap) > .1 * gap ||
                    Math.Abs(line.Left - group[0].Left) > .008 || Math.Abs(line.Right - group[0].Right) > .008).Any())
            { yield return null; yield break; }
            yield return (new(group[0].Left, group[0].Y, group[0].Right, group[4].Y), group[5].Y);
        }
    }

    // The treble staff's bottom line is E4; F is sharpened by the opening key signature.
    internal static int Pitch(int step, int? alteration = null)
    {
        var position = step + 2;
        var index = ((position % 7) + 7) % 7;
        return 60 + 12 * ((position - index) / 7) + Scale[index] + (alteration ?? (index == 3 ? 1 : 0));
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

    private static string TextTab(List<Note> notes, Fingering[] fingers, int measures, int bpm, bool complete, string? stop)
    {
        var text = new StringBuilder($"Melody-only guitar TAB — {(complete ? "" : "first ")}{measures} measures\n" +
            $"Standard tuning, no capo · 4/4 · {bpm} BPM\nEach column is a sixteenth note. Digits mark attacks; ~ is a held note.\n");
        if (stop is not null) text.AppendLine($"Stopped at {stop}");
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
