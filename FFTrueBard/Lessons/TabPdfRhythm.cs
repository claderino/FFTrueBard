using System.Text.RegularExpressions;

namespace FFTrueBard.Lessons;

internal readonly record struct TabPdfPoint(double X, double Y);
internal readonly record struct TabPdfGlyph(char Code, double X, double Y, bool Legacy = false);
internal readonly record struct TabPdfArc(double Left, double Top, double Right, double Bottom, int Segments = 0);
internal readonly record struct TabPdfContour(double Left, double Top, double Right, double Bottom, int Points, int CurvePoints);
internal sealed record TabPdfStroke(bool Filled, TabPdfPoint[] Points);
internal sealed record TabPdfNotation(TabPdfGlyph[] Glyphs, TabPdfStroke[] Strokes, TabPdfArc[] Arcs,
    TabPdfContour[]? Contours = null);

internal static partial class TabPdfRhythm
{
    internal sealed record Reading(float?[] Beats, float[] RestsBefore, bool HasUnsupportedMarks);
    internal sealed record Settings(int? Bpm, int? Capo, string? Tuning);
    private readonly record struct Stem(double X, double Top, double Bottom);
    private readonly record struct Beam(double Left, double LeftY, double Right, double RightY)
    {
        public double HeightAt(double x) => LeftY + (RightY - LeftY) * Math.Clamp((x - Left) / (Right - Left), 0, 1);
    }

    public static Reading Read(LessonPdfRenderer.Image image, TabDraftStaff draft)
    {
        var durations = new float?[draft.Columns.Length];
        var rests = new float[durations.Length];
        if (image.TabNotation is not { } notation) return new(durations, rests, false);
        var staff = draft.Staff;
        var dy = (staff.Bottom - staff.Top) / 5d;
        var dx = dy * image.Height / image.Width;
        if (draft.OutlinedFrets) return ReadBelow(notation, draft, dx, dy);
        var legacy = notation.Glyphs.Any(g => g.Legacy);
        var musicGap = draft.NotationStaff is { } music ? (music.Bottom - music.Top) / 4 : dy * .875;
        var musicGapX = musicGap * image.Height / image.Width;
        var minY = draft.NotationStaff is { } above ? Math.Min(staff.Top - 13 * dy, above.Top - 6 * musicGap) : staff.Top - 13 * dy;
        var maxY = staff.Top - .8 * dy;
        var band = notation.Glyphs.Where(glyph => glyph.X >= staff.Left && glyph.X <= staff.Right &&
            glyph.Y >= minY && glyph.Y <= staff.Top - 1.5 * dy).ToArray();
        var outlines = notation.Arcs.Where(a => a.Left >= staff.Left && a.Right <= staff.Right + dx &&
            a.Top >= minY && a.Bottom <= maxY).ToArray();
        // Some engravers draw every filled notehead as one closed four-curve oval.
        var vectorHeads = outlines.Where(a => a.Segments is 13 or 14 &&
            (a.Right - a.Left) / musicGapX is >= 1.05 and <= 1.32 &&
            (a.Bottom - a.Top) / musicGap is >= .77 and <= 1.02)
            .Select(a => new TabPdfGlyph('\ue0a4', a.Left, (a.Top + a.Bottom) / 2)).ToArray();
        var stems = new List<Stem>();
        var beams = new List<Beam>();
        var barlines = new List<double>();
        foreach (var path in notation.Strokes)
        {
            if (!path.Filled)
            {
                var first = path.Points[0];
                var last = path.Points[1];
                var top = Math.Min(first.Y, last.Y);
                var bottom = Math.Max(first.Y, last.Y);
                var x = (first.X + last.X) / 2;
                if (Math.Abs(first.X - last.X) > dx * .17 || x < staff.Left || x > staff.Right) continue;
                if (top <= staff.Top + dy * .5 && bottom >= staff.Bottom - dy * .5)
                    barlines.Add(x);
                else if (bottom - top >= dy * 1.2 && bottom - top <= dy * (legacy ? 8.5 : 5) &&
                    top >= minY && bottom <= maxY)
                    stems.Add(new(x, top, bottom));
                continue;
            }
            var p = path.Points;
            if (p.Length == 5 && (Math.Abs(p[4].X - p[0].X) > dx * .02 || Math.Abs(p[4].Y - p[0].Y) > dy * .02))
                continue;
            var left = p.Take(4).OrderBy(v => v.X).Take(2).ToArray();
            var right = p.Take(4).OrderByDescending(v => v.X).Take(2).ToArray();
            var lx = (left[0].X + left[1].X) / 2;
            var rx = (right[0].X + right[1].X) / 2;
            var ly = (left[0].Y + left[1].Y) / 2;
            var ry = (right[0].Y + right[1].Y) / 2;
            var lh = Math.Abs(left[0].Y - left[1].Y);
            var rh = Math.Abs(right[0].Y - right[1].Y);
            if (rx - lx is > 0 && rx - lx < dx * .22)
            {
                var top = p.Min(v => v.Y);
                var bottom = p.Max(v => v.Y);
                if (lx >= staff.Left && rx <= staff.Right && top >= minY && bottom <= maxY &&
                    (bottom - top) / musicGap is >= 2 and <= 7)
                    stems.Add(new((lx + rx) / 2, top, bottom));
            }
            if (lx >= staff.Left - dx && rx <= staff.Right + dx && rx - lx > dx * .45 &&
                Math.Abs(left[0].X - left[1].X) < .2 * dx && Math.Abs(right[0].X - right[1].X) < .2 * dx &&
                lh >= .16 * dy && lh <= .7 * dy && Math.Abs(lh - rh) < .14 * dy &&
                Math.Abs(ly - ry) < (rx - lx) * 1.5 && Math.Max(ly, ry) < staff.Top - .5 * dy)
                beams.Add(new(lx, ly, rx, ry));
        }

        var groups = draft.Columns.Select(_ => new List<TabPdfGlyph>()).ToArray();
        foreach (var head in band.Where(g => g.Code is '\ue0a2' or '\ue0a3' or '\ue0a4' or '\uf4bd' or '\uf4be' ||
            g.Legacy && g.Code is 'B' or 'C').Concat(vectorHeads))
        {
            var index = NearestColumn(head.X);
            if (index >= 0) groups[index].Add(head);
        }

        int NearestColumn(double x)
        {
            var index = -1;
            var distance = .95 * dx;
            for (var i = 0; i < draft.Columns.Length; i++)
            {
                var column = draft.Columns[i];
                var candidate = Math.Abs(column.X + .4 * column.Width - x);
                if (candidate < distance) { distance = candidate; index = i; }
            }
            return index;
        }

        var mixedVoices = false;
        for (var i = 0; i < groups.Length; i++)
        {
            var lengths = new List<float>();
            foreach (var head in groups[i])
            {
                var duration = head.Code == '\ue0a2' ? 4f : (head.Code is '\ue0a3' or '\uf4bd' ? 2f : 1f);
                var candidates = stems.SelectMany(stem => new[]
                {
                    (Stem: stem, Tip: stem.Top, Match: Math.Abs(stem.X - head.X - .83 * dx) / dx + Math.Abs(stem.Bottom - head.Y) / dy),
                    (Stem: stem, Tip: stem.Bottom, Match: Math.Abs(stem.X - head.X - .06 * dx) / dx + Math.Abs(stem.Top - head.Y) / dy),
                }).Where(stem => stem.Match < .95).OrderBy(stem => stem.Match).ToArray();
                // A head in a chord may share a stem with an outside head; the latter supplies its duration.
                if (duration != 4 && candidates.Length == 0) continue;
                if (duration == 1)
                {
                    var stem = candidates[0];
                    if (BeamLength(stem.Stem.X, stem.Tip) is { } length)
                        duration = length;
                    else
                    {
                        var flags = band.Where(g => g.Code is >= '\ue240' and <= '\ue247' &&
                            Math.Abs(g.X - stem.Stem.X) < .65 * dx && Math.Abs(g.Y - stem.Tip) < dy * .8).ToArray();
                        if (flags.Length > 0) duration = 1f / (1 << (1 + (flags[0].Code - '\ue240') / 2));
                        else if (vectorHeads.Contains(head) && outlines.Any(a => a.Segments is >= 45 and <= 85 &&
                            (a.Right - a.Left) / musicGapX is >= .75 and <= 1.1 &&
                            (a.Bottom - a.Top) / musicGap is >= 2.1 and <= 2.4 &&
                            Math.Abs(a.Left - stem.Stem.X) < .25 * musicGapX &&
                            (stem.Tip > head.Y ? Math.Abs(a.Bottom - stem.Tip) : Math.Abs(a.Top - stem.Tip)) < .4 * musicGap))
                            duration = .5f;
                    }
                }
                if (band.Any(g => g.Code == '\ue1e7' && g.X - head.X is > 0 &&
                    g.X - head.X < dx * 2 && Math.Abs(g.Y - head.Y) < dy * .52))
                    duration *= 1.5f;
                lengths.Add(duration);
            }
            if (groups[i].Count > 0 && groups[i].Count < draft.Columns[i].Frets.Count(fret => fret >= 0))
            {
                // A PDF text layer can suppress a duplicate notehead when two heads overlap.
                // A second stem with a visible beam still carries rhythm evidence at that chord.
                foreach (var head in groups[i])
                foreach (var stem in stems)
                {
                    if (Math.Abs(stem.X - head.X - .06 * dx) < .25 * dx &&
                        Math.Abs(stem.Top - head.Y) < dy && BeamLength(stem.X, stem.Bottom) is { } down)
                        lengths.Add(down);
                    if (Math.Abs(stem.X - head.X - .83 * dx) < .25 * dx &&
                        Math.Abs(stem.Bottom - head.Y) < dy && BeamLength(stem.X, stem.Top) is { } up)
                        lengths.Add(up);
                }
            }
            if (lengths.Count > 0)
            {
                durations[i] = lengths.Min();
                mixedVoices |= lengths.Any(length => length != durations[i]);
            }
        }

        float? BeamLength(double x, double tip)
        {
            var covering = beams.Where(beam => x >= beam.Left - .19 * dx && x <= beam.Right + .19 * dx)
                .Select(beam => beam.HeightAt(x)).Where(y => Math.Abs(y - tip) <= dy * 1.25)
                .DistinctBy(y => Math.Round(y / (.12 * dy))).OrderBy(y => Math.Abs(y - tip)).ToArray();
            return covering.Length > 0 && Math.Abs(covering[0] - tip) <= dy * .42
                ? 1f / (1 << Math.Min(covering.Length, 4)) : null;
        }

        var unsupported = mixedVoices || notation.Glyphs.Any(g => g.X >= staff.Left && g.X <= staff.Right &&
            g.Y >= staff.Top - 13 * dy && g.Y <= staff.Bottom + dy &&
            g.Code is >= '\ue880' and <= '\ue89f' or >= '\ue040' and <= '\ue049');
        unsupported |= notation.Arcs.Any(arc => arc.Left >= staff.Left && arc.Right <= staff.Right + dx &&
            arc.Top >= staff.Top - 13 * dy && arc.Bottom <= staff.Bottom + 1.5 * dy &&
            arc.Right - arc.Left > 1.65 * dx && arc.Bottom - arc.Top < 1.4 * dy);
        unsupported |= notation.Glyphs.Any(g => (g.Legacy ? g.Code == '+' : g.Code is 'x' or 'X') &&
            g.X >= staff.Left && g.X <= staff.Right &&
            g.Y >= staff.Top && g.Y <= staff.Bottom + .7 * dy);
        var explicitRests = band.Where(g => g.Code is >= '\ue4e3' and <= '\ue4e8')
            .Concat(vectorHeads.Length == 0 ? [] : outlines.Where(a => a.Segments == 62 &&
                (a.Right - a.Left) / musicGapX is >= .65 and <= .76 &&
                (a.Bottom - a.Top) / musicGap is >= 1.15 and <= 1.31)
                .Select(a => new TabPdfGlyph('\ue4e6', (a.Left + a.Right) / 2, (a.Top + a.Bottom) / 2)))
            .DistinctBy(g => (Math.Round(g.X / (.2 * dx)), g.Code)).ToArray();
        foreach (var rest in explicitRests)
        {
            var next = Array.FindIndex(draft.Columns, c => c.X > rest.X + dx * .25);
            // Multiple voice notation can contain rests while another voice is sounding.
            // Keep those cases as a review task instead of inserting a spurious music-wide gap.
            if (next < 0 || mixedVoices ||
                barlines.Any(x => x > rest.X && x < draft.Columns[next].X) ||
                draft.Columns.Any(c => Math.Abs(c.X + c.Width / 2 - rest.X) < dx * .7))
            {
                unsupported = true;
                continue;
            }
            var restBeats = 4f / (1 << (rest.Code - '\ue4e3'));
            if (band.Any(g => g.Code == '\ue1e7' && g.X > rest.X && g.X - rest.X < 2 * dx &&
                Math.Abs(g.Y - rest.Y) < dy * .6)) restBeats *= 1.5f;
            rests[next] += restBeats;
        }
        return new(durations, rests, unsupported);
    }

    private static Reading ReadBelow(TabPdfNotation notation, TabDraftStaff draft, double dx, double dy)
    {
        var staff = draft.Staff;
        var stems = new List<Stem>();
        var beams = new List<Beam>();
        foreach (var path in notation.Strokes)
        {
            var left = path.Points.Min(p => p.X);
            var right = path.Points.Max(p => p.X);
            var top = path.Points.Min(p => p.Y);
            var bottom = path.Points.Max(p => p.Y);
            if (left < staff.Left || right > staff.Right || top < staff.Bottom + .45 * dy ||
                bottom > staff.Bottom + 3.5 * dy) continue;
            if (right - left < .15 * dx && (bottom - top) / dy is >= .65 and <= 1.9)
                stems.Add(new((left + right) / 2, top, bottom));
            else if (path.Filled && right - left >= .45 * dx && (bottom - top) / dy is >= .15 and <= .38)
                beams.Add(new(left, (top + bottom) / 2, right, (top + bottom) / 2));
        }

        float? LengthAt(double x)
        {
            var matched = stems.Where(s => Math.Abs(s.X - x) < .27 * dx)
                .OrderByDescending(s => s.Bottom - s.Top).FirstOrDefault();
            if (matched == default) return null;
            var layers = beams.Where(b => x >= b.Left - .25 * dx && x <= b.Right + .25 * dx &&
                Math.Abs(b.LeftY - matched.Bottom) < .86 * dy)
                .DistinctBy(b => Math.Round(b.LeftY / (.13 * dy))).Count();
            return layers switch
            {
                0 when matched.Bottom - matched.Top >= 1.25 * dy => 1,
                0 => 2,
                >= 1 and <= 3 => 1f / (1 << layers),
                _ => null,
            };
        }

        var lengths = draft.Columns.Select(c => LengthAt(c.X + c.Width / 2)).ToArray();
        var rests = new float[lengths.Length];
        foreach (var contour in notation.Contours ?? [])
        {
            if (contour.Left < staff.Left || contour.Right > staff.Right || contour.Top < staff.Top - dy ||
                contour.Bottom > staff.Bottom + dy) continue;
            var height = (contour.Bottom - contour.Top) / dy;
            var width = (contour.Right - contour.Left) / dx;
            var candidate = (contour.CurvePoints, height, width) switch
            {
                (30, >= 1.75 and <= 1.98, >= .82 and <= .99) => .25f,
                (15, >= 1.11 and <= 1.38, >= .64 and <= .84) => .5f,
                (24, >= 1.87 and <= 2.13, >= .65 and <= .85) => 1f,
                _ => 0,
            };
            if (candidate == 0) continue;
            var center = (contour.Left + contour.Right) / 2;
            // Confirm the rest with an independently drawn rhythm flag/beam.
            if (LengthAt(center) != candidate) continue;
            var next = Array.FindIndex(draft.Columns, column => column.X > contour.Right);
            if (next >= 0) rests[next] += candidate;
        }
        // This format can place ties, rests, and empty opening measures in the same
        // system. A partial contour read is useful in the editor, not for unattended playback.
        return new(lengths, rests, true);
    }

    public static Settings ReadSettings(LessonPdfRenderer.Image image, double firstTabTop, LessonTabStaff[]? staves = null)
    {
        var lines = TextLines(image, firstTabTop).ToArray();
        static T? Single<T>(IEnumerable<T> values) where T : struct
        {
            var items = values.Distinct().Take(2).ToArray();
            return items.Length == 1 ? items[0] : null;
        }
        var markedTempos = lines.SelectMany(line => QuarterTempo().Matches(line).Cast<Match>())
            .Select(match => int.Parse(match.Groups[1].Value));
        var graphicTempos = ReadGraphicTempos(image, firstTabTop);
        var bpm = Single(markedTempos.Concat(graphicTempos).Where(value => value is >= 20 and <= 300));
        var capo = Single(lines.SelectMany(line => CapoText().Matches(line).Cast<Match>()).Select(match =>
        {
            var fret = match.Groups[1].Value;
            if (int.TryParse(fret, out var number)) return number;
            return Array.IndexOf(new[] { "NONE", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII" },
                fret.ToUpperInvariant());
        }).Where(value => value is >= 0 and <= 12));
        var dropD = lines.Any(line => DropDText().IsMatch(line));
        var standard = lines.Any(line => line.Contains("standardtuning", StringComparison.OrdinalIgnoreCase));
        var namedTuning = dropD == standard ? null : dropD ? "38 45 50 55 59 64" : "40 45 50 55 59 64";
        var stringLabels = (staves ?? []).Select(staff => ReadStringLabels(image, staff)).OfType<string>()
            .Distinct().Take(2).ToArray();
        if (stringLabels.Length > 1) return new(bpm, capo, null);
        var printedTuning = stringLabels.FirstOrDefault();
        return new(bpm, capo, namedTuning is not null && printedTuning is not null && namedTuning != printedTuning
            ? null : printedTuning ?? namedTuning);
    }

    private static string? ReadStringLabels(LessonPdfRenderer.Image image, LessonTabStaff staff)
    {
        if (image.TabNotation is not { } notation) return null;
        var dy = (staff.Bottom - staff.Top) / 5;
        var dx = dy * image.Height / image.Width;
        int[] standard = [64, 59, 55, 50, 45, 40];
        var pitches = new int[6];
        for (var s = 0; s < 6; s++)
        {
            var target = staff.Top + (s + .36) * dy;
            var line = notation.Glyphs.Where(g => !g.Legacy && g.X >= staff.Left - 4 * dx &&
                g.X < staff.Left + .15 * dx && Math.Abs(g.Y - target) < .41 * dy)
                .OrderBy(g => g.X).ToArray();
            var notes = line.Where(g => g.Code is >= 'A' and <= 'G').ToArray();
            if (notes.Length != 1) return null;
            var note = notes[0];
            var semitone = note.Code switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
            var signs = line.Where(g => g.X > note.X && g.X - note.X < 1.9 * dx &&
                g.Code is '#' or '♯' or 'b' or '♭' or '\ue260' or '\ue262').ToArray();
            if (signs.Length > 1) return null;
            if (signs.Length == 1) semitone += signs[0].Code is '#' or '♯' or '\ue262' ? 1 : -1;
            else if (notation.Arcs.Any(a => a.Segments is 73 or 74 && a.Left > note.X && a.Left - note.X < 1.9 * dx &&
                Math.Abs(a.Bottom - note.Y) < .45 * dy && (a.Right - a.Left) / dx is >= .32 and <= .52 &&
                (a.Bottom - a.Top) / dy is >= 1 and <= 1.2))
                semitone--;
            var pitch = Enumerable.Range(standard[s] - 4, 9).FirstOrDefault(p => (p - semitone) % 12 == 0);
            if (pitch == 0 || s > 0 && pitch >= pitches[s - 1]) return null;
            pitches[s] = pitch;
        }
        return string.Join(" ", pitches.Reverse());
    }

    public static bool HasUnresolvedNavigation(LessonPdfRenderer.Image image) =>
        TextLines(image, 1).Any(line => NavigationText().IsMatch(line));

    private static IEnumerable<int> ReadGraphicTempos(LessonPdfRenderer.Image image, double beforeY)
    {
        if (image.TabNotation is not { } notation) yield break;
        var aspect = (double)image.Height / image.Width;
        foreach (var equals in notation.Glyphs.Where(g => g.Code == '=' && g.Y > 0 && g.Y < beforeY))
        {
            var number = new string(notation.Glyphs.Where(g => char.IsAsciiDigit(g.Code) && !g.Legacy &&
                g.X > equals.X && g.X - equals.X < .065 && Math.Abs(g.Y - equals.Y) < .0018)
                .OrderBy(g => g.X).Take(4).Select(g => g.Code).ToArray());
            if (number.Length is < 2 or > 3 || !int.TryParse(number, out var tempo)) continue;
            if (notation.Glyphs.Any(g => g.Legacy && g.Code == 'h' && g.X < equals.X &&
                equals.X - g.X < .026 && Math.Abs(g.Y - equals.Y) < .004))
            {
                yield return tempo;
                continue;
            }
            foreach (var shape in notation.Arcs.Where(a => a.Right < equals.X && equals.X - a.Right < .023 &&
                Math.Abs(a.Bottom - equals.Y) < .007))
            {
                var w = (shape.Right - shape.Left) / aspect;
                var h = shape.Bottom - shape.Top;
                // A combined unflagged quarter glyph is narrow with a tall stem.
                var combined = shape.Segments is >= 14 and <= 25 && h > 2.3 * w && h < 3.5 * w;
                var separate = shape.Segments is 13 or 14 && h > .65 * w && h < .9 * w &&
                    notation.Strokes.Any(s => !s.Filled &&
                        Math.Abs(s.Points[0].X - s.Points[1].X) < .15 * w * aspect &&
                        Math.Abs(s.Points[0].X - shape.Right) < .19 * w * aspect &&
                        Math.Abs(s.Points.Max(p => p.Y) - shape.Bottom) < .8 * h &&
                        s.Points.Min(p => p.Y) < shape.Top - 1.7 * h &&
                        !notation.Arcs.Any(a => a != shape && Math.Abs(a.Top - s.Points.Min(p => p.Y)) < h &&
                            Math.Abs(a.Left - shape.Right) < .35 * w * aspect));
                if (!combined && !separate) continue;
                yield return tempo;
                break;
            }
        }
    }

    private static IEnumerable<string> TextLines(LessonPdfRenderer.Image image, double beforeY)
    {
        if (image.TabNotation is not { } notation) yield break;
        var text = notation.Glyphs.Where(g => g.X > 0 && g.Y > 0 && g.Y < beforeY && !char.IsWhiteSpace(g.Code))
            .OrderBy(g => g.Y).ToArray();
        for (var i = 0; i < text.Length;)
        {
            var j = i + 1;
            while (j < text.Length && text[j].Y - text[i].Y < .0018) j++;
            var ordered = text[i..j].OrderBy(g => g.X).ToArray();
            var start = 0;
            for (var k = 1; k < ordered.Length; k++)
            {
                if (ordered[k].X - ordered[k - 1].X < .04) continue;
                yield return string.Concat(ordered[start..k].Select(g => g.Code));
                start = k;
            }
            yield return string.Concat(ordered[start..].Select(g => g.Code));
            i = j;
        }
    }

    [GeneratedRegex(@"[\ueca5\ue1d5]=([0-9]{2,3})(?![0-9])")]
    private static partial Regex QuarterTempo();

    [GeneratedRegex(@"capo[.:]?(?:on|fret|at)?(1[0-2]|[0-9]|[IVX]+|none)(?![0-9IVX])", RegexOptions.IgnoreCase)]
    private static partial Regex CapoText();

    [GeneratedRegex(@"(?:drop-?D(?![a-z])|⑥=D(?![#♯a-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex DropDText();

    [GeneratedRegex(@"(?:D\.S\.|D\.C\.|adlib\.|alfine|alcoda|rall\.|rit\.|accel\.|atempo|letring)", RegexOptions.IgnoreCase)]
    private static partial Regex NavigationText();
}
