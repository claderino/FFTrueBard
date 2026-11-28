using FFTrueBard.Lessons;

internal static class PianoScoreChecks
{
    public static void Run(string pdfHash)
    {
        var first = Page(pdfHash, opening: true);
        var second = Page(pdfHash, opening: false);
        var melody = PianoMelodyTab.ReadScore(page => page == 1 ? first : second);
        var guide = TabPdfGuide.FromDocument(melody.Document);
        Check(melody is { Measures: 3, Bpm: 120, StopReason: not null } &&
            melody.StopReason.Contains("Page 2, system 1, measure 4: a tie", StringComparison.Ordinal),
            $"Expected the first tie at measure 4 after three complete measures: {melody.Measures} measures. {melody.StopReason}");
        Check(guide.Rows.Select(r => r.Page).SequenceEqual([1, 2]) &&
            guide.RowAt(1.5)?.Page == 1 && guide.RowAt(2.25) is null &&
            guide.RowAt(4.25)?.Page == 2 && guide.RowAt(6.25) is null && guide.End == 6,
            "The piano preview must hide the cursor during the whole-bar rest and keep time across pages");

        int[] open = [64, 59, 55, 50, 45, 40];
        var heard = melody.Document.Rows.SelectMany(r => r.Attacks).Select(a =>
        {
            var f = a.Frets.Single();
            return open[f.String - 1] + f.Number;
        });
        Check(heard.SequenceEqual([65, 65, 67, 65, 66, 66, 67, 66]),
            "A treble accidental lasts until the barline, and the next page retains the key signature but not the accidental");
        Check(!melody.Document.Reviewed && melody.Text.Contains("first 3 measures", StringComparison.Ordinal),
            "An incomplete piano preview remains unreviewed and reports its coverage");
        var ending = TabPdfGuide.FromDocument(melody.Document with { Duration = 6.5 });
        Check(ending.RowAt(6.25) is null && ending.End == 6.5,
            "Trailing rests extend the audio without inventing a cursor position");
    }

    // An original two-page notation fixture. Bounding boxes are in staff spaces
    // so the expected times and pitches do not depend on any personal PDF.
    private static LessonPdfRenderer.Image Page(string hash, bool opening)
    {
        const double left = .1, right = .9, top = .2, step = .01, bass = .31;
        var strokes = new List<TabPdfStroke>();
        var shapes = new List<TabPdfContour>();
        void Line(double x, double y, double x2, double y2) => strokes.Add(new(false, [new(x, y), new(x2, y2)]));
        void Shape(double x, double center, double width, double height, int points, int curves) =>
            shapes.Add(new(x, center - height * step / 2, x + width * step, center + height * step / 2, points, curves));
        for (var i = 0; i < 5; i++)
        {
            Line(left, top + i * step, right, top + i * step);
            Line(left, bass + i * step, right, bass + i * step);
        }
        Line(.5, top, .5, bass + 4 * step);
        Line(right, top, right, bass + 4 * step);
        Shape(.11, top + 3.4 * step, 1.3, 2.4, 61, 60); // treble
        Shape(.14, top, .98, 2.66, 58, 24); // F sharp
        if (opening)
        {
            Shape(.17, top + step, 1.73, 2, 90, 72);
            Shape(.17, top + 3 * step, 1.73, 2, 90, 72);
            Shape(.237, top + 3.5 * step, .69, 2.60, 22, 12); // F natural
        }
        Notes(.25);
        if (opening)
            Shape(.694, top + 1.3 * step, 1.3, .54, 17, 12); // whole-bar rest
        else
        {
            Notes(.55);
            Shape(.552, top + 4.3 * step, 6.5, .35, 13, 12); // unsupported tie
        }
        void Notes(double start)
        {
            for (var i = 0; i < 4; i++)
            {
                var x = start + .065 * i;
                var y = top + (i == 2 ? 3 : 3.5) * step;
                Shape(x, y, 1.3, 1.06, 25, 24);
                Line(x + .012, y - .032, x + .012, y);
            }
        }
        var tempo = opening ? "\ueca5=120" : "";
        return new(1000, 1000, [], null)
        {
            PageCount = 2, PdfSha256 = hash,
            TabNotation = new(tempo.Select((c, i) => new TabPdfGlyph(c, .15 + .01 * i, .16)).ToArray(),
                strokes.ToArray(), [], shapes.ToArray()),
        };
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
