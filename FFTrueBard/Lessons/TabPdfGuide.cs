using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;

namespace FFTrueBard.Lessons;

/// <summary>A local transcription: audio and the score cursor share the same attacks.</summary>
internal sealed class TabPdfGuide
{
    internal sealed record Fret(int String, int Number, double Hold, double Delay = 0);
    internal sealed record Attack(double Start, double End, double X, double Width, int Pass, Fret[] Frets);
    internal sealed record Row(int Page, double Left, double Top, double Right, double Bottom, Attack[] Attacks);
    internal sealed record Document(int SchemaVersion, string PdfSha256, bool Reviewed, string Coverage,
        int[] Tuning, int Capo, Row[] Rows);
    internal sealed record ScoreRow(int Page, LessonTabStaff Staff, LessonScoreCursor Cursor);
    internal enum SaveMode { Create, Append, Replace }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly TabSynthNote[] notes;
    public ScoreRow[] Rows { get; }
    public string Coverage { get; }
    public double End { get; }

    private TabPdfGuide(ScoreRow[] rows, TabSynthNote[] notes, string coverage, double end)
    {
        Rows = rows;
        this.notes = notes;
        Coverage = coverage;
        End = end;
    }

    public static TabPdfGuide Load(string pdf, string transcription)
    {
        var document = ReadReviewed(transcription);
        RequireMatchingPdf(pdf, document);
        return FromDocument(document);
    }

    private static Document ReadReviewed(string transcription)
    {
        using var file = File.OpenRead(transcription);
        if (file.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("The TAB guide must be a non-empty file of at most 1 MiB.");
        var document = JsonSerializer.Deserialize<Document>(file, Json);
        if (document is not { Reviewed: true })
            throw new InvalidDataException("The local TAB guide has not been reviewed.");
        return document;
    }

    internal static string SaveReviewed(string pdf, Document draft, SaveMode mode)
    {
        var document = draft with { Reviewed = true };
        RequireMatchingPdf(pdf, document);
        var path = Path.ChangeExtension(pdf, ".tab-guide.json");
        if (mode == SaveMode.Append)
        {
            var original = ReadReviewed(path);
            RequireMatchingPdf(pdf, original);
            var duration = FromDocument(original).End;
            if (!original.Tuning.SequenceEqual(draft.Tuning) || original.Capo != draft.Capo)
                throw new InvalidDataException("To append a passage, use the existing guide's tuning and capo.");
            if (draft.Rows.Any(row => original.Rows.Any(old => old.Page == row.Page &&
                old.Top < row.Bottom && row.Top < old.Bottom && old.Left < row.Right && row.Left < old.Right)))
                throw new InvalidDataException("This staff is already covered. Append a different staff or explicitly replace the guide.");
            var added = draft.Rows.Select(row => row with
            {
                Attacks = row.Attacks.Select(note => note with { Start = note.Start + duration, End = note.End + duration }).ToArray(),
            }).ToArray();
            document = original with
            {
                Coverage = $"{original.Rows.Length + added.Length} reviewed staff passages",
                Rows = [.. original.Rows, .. added],
            };
        }
        _ = FromDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > 1024 * 1024)
            throw new InvalidDataException("The reviewed guide exceeds 1 MiB.");
        if (mode == SaveMode.Create && File.Exists(path))
            throw new IOException("A guide already exists. Choose append or replacement with backup to change it.");
        var id = Guid.NewGuid().ToString("N");
        var pending = path + "." + id + ".writing";
        string? backup = null;
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(bytes);
            if (mode == SaveMode.Append || (mode == SaveMode.Replace && File.Exists(path)))
            {
                backup = Path.ChangeExtension(pdf, $".backup-{id}.tab-guide.json");
                File.Replace(pending, path, backup);
            }
            else File.Move(pending, path);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
        return backup ?? "";
    }

    private static void RequireMatchingPdf(string pdf, Document document)
    {
        using var source = File.OpenRead(pdf);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(source)), document.PdfSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local TAB guide doesn't match this PDF. Rescan or use the matching edition.");
    }

    internal static TabPdfGuide FromDocument(Document document)
    {
        if (document is not { SchemaVersion: 1, Coverage.Length: >= 1 and <= 150,
                PdfSha256.Length: 64, Tuning.Length: 6, Capo: >= 0 and <= 12, Rows.Length: >= 1 and <= 512 } ||
            document.Tuning.Any(open => open is < 24 or > 84))
            throw new InvalidDataException("The local TAB guide is not a supported six-string transcription.");

        var rows = new List<ScoreRow>(document.Rows.Length);
        var notes = new List<TabSynthNote>();
        var positions = new List<LessonScoreMark>();
        var end = 0d;
        foreach (var row in document.Rows)
        {
            if (row is not { Page: >= 1, Left: >= 0, Right: <= 1, Top: >= 0, Bottom: <= 1,
                    Attacks.Length: >= 1 and <= 1024 } || row.Left >= row.Right || row.Top >= row.Bottom)
                throw new InvalidDataException("The TAB guide contains an invalid page or staff.");
            var staff = new LessonTabStaff(row.Left, row.Top, row.Right, row.Bottom);
            var marks = new LessonScoreMark[row.Attacks.Length];
            for (var i = 0; i < row.Attacks.Length; i++)
            {
                var attack = row.Attacks[i];
                if (attack is not { Frets.Length: >= 1 and <= 6 } || notes.Count + attack.Frets.Length > 20_000)
                    throw new InvalidDataException("The TAB guide contains an invalid chord or too many notes.");
                var mark = new LessonScoreMark(attack.Start, attack.End, attack.X, row.Top, attack.Width,
                    row.Bottom - row.Top, attack.Pass);
                if (!mark.IsValid(0, 600) || attack.X < row.Left || attack.X + attack.Width > row.Right)
                    throw new InvalidDataException("The TAB guide contains an invalid note position or time.");
                marks[i] = mark;
                positions.Add(mark);
                end = Math.Max(end, attack.End);
                var strings = 0;
                foreach (var fret in attack.Frets)
                {
                    if (fret is not { String: >= 1 and <= 6, Number: >= 0 and <= 24, Delay: >= 0 and <= .25, Hold: > 0 and <= 60 } ||
                        (strings & (1 << fret.String)) != 0)
                        throw new InvalidDataException("The TAB guide contains an invalid or duplicate fret.");
                    strings |= 1 << fret.String;
                    var start = attack.Start + fret.Delay;
                    var stop = start + fret.Hold;
                    if (start >= attack.End || stop > 600)
                        throw new InvalidDataException("The TAB guide contains a fret outside its timing bounds.");
                    end = Math.Max(end, stop);
                    notes.Add(new(start, stop, document.Tuning[6 - fret.String] + document.Capo + fret.Number, 82));
                }
            }
            var map = new LessonScorePlayhead(marks.Any(mark => mark.Pass > 0) ? 2 : 1, 0, row.Page, true, "", marks);
            var cursor = new LessonScoreCursor(staff, 0, 600, map, hideGaps: true);
            if (!cursor.NoteSynced)
                throw new InvalidDataException("The TAB guide contains notes out of order on a staff.");
            rows.Add(new(row.Page, staff, cursor));
        }

        // A held bass note can ring into the next attack, but two positions cannot own the same cursor time.
        var previousEnd = 0d;
        foreach (var mark in positions.OrderBy(mark => mark.Start))
        {
            if (mark.Start < previousEnd)
                throw new InvalidDataException("The TAB guide contains overlapping cursor positions.");
            previousEnd = mark.End;
        }
        if (end < .1)
            throw new InvalidDataException("The TAB guide is too short to play.");
        return new(rows.ToArray(), notes.ToArray(), document.Coverage, end);
    }

    public ScoreRow? RowAt(double time) => Rows.FirstOrDefault(row => row.Cursor.ShowsNoteAt(time));
    internal WaveStream Render() => TabMidiGuide.Render(notes, End);
}
