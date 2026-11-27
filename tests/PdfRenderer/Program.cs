using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFTrueBard.Lessons;

if (args.Length == 0)
    throw new ArgumentException("Pass the built plugin directory, optionally followed by a supported local PDF.");
var pluginDirectory = Path.GetFullPath(args[0]);

// Model a host that already owns the process-wide COM marshaller. PDF rendering
// must work without registering another instance or calling the host's marshaller.
ComWrappers.RegisterForMarshalling(new HostMarshaller());
using var folder = new ScratchDirectory();
var fixture = Path.Combine(folder.Path, "Étude 楽譜.pdf");
WriteTestPdf(fixture);

var full = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, null);
Check((full.Width, full.Height, full.PageCount) == (1800, 900, 1), "Full page dimensions and document page count");
CheckPixel(full, .25, .25, 0, 0, 255);
CheckPixel(full, .75, .25, 255, 255, 255);
CheckPixel(full, .75, .75, 255, 0, 0);

var crop = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, new(.5, .5, .5, .5));
Check((crop.Width, crop.Height) == (1800, 900), "Excerpt dimensions");
CheckPixel(crop, .5, .5, 255, 0, 0);
Check(full.AudioSeconds is null, "Rendering an unmatched page doesn't interpret an audio cue");
var withAudio = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, null, new(0, 0, 1, 1));
Check(withAudio.AudioSeconds == 71, "The printed time is read from the bottom-left marker region");
var withoutMarker = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, null, new(.5, .5, .5, .5));
Check(withoutMarker.AudioSeconds is null, "The renderer doesn't guess when there is no printed marker");
Reject(() => LessonPdfRenderer.Render(pluginDirectory, fixture, 2, null), "outside this PDF");
Reject(() => LessonPdfRenderer.Render(pluginDirectory, fixture, 1, new(.9, .9, .5, .5)), "crop");
var damaged = Path.Combine(folder.Path, "damaged.pdf");
File.WriteAllText(damaged, "not a PDF");
Reject(() => LessonPdfRenderer.Render(pluginDirectory, damaged, 1, null), "could not be opened");
Console.WriteLine("PDF render checks passed: host COM isolation, Unicode paths, full page, crop, and invalid input.");

var draftFixture = Path.Combine(folder.Path, "TAB practice.pdf");
var staffInk = string.Concat(Enumerable.Range(0, 6).Select(i => $"40 {150 - i * 9} m 425 {150 - i * 9} l S\n"));
WriteTestPdf(draftFixture, $"0 G 0.5 w\n{staffInk}" +
    "BT /F1 8 Tf 100 147 Td (12) Tj ET\nBT /F1 8 Tf 100 120 Td (3) Tj ET\nBT /F1 8 Tf 150 102 Td (0) Tj ET\n");
var draftImage = LessonPdfRenderer.Render(pluginDirectory, draftFixture, 1, null, readTabDigits: true);
var candidates = TabPdfDraft.Detect(draftImage);
Check(draftImage.PdfSha256 is { Length: 64 } && candidates is [{ Columns: [var chord, var bass] }] &&
    chord.Frets[0] == 12 && chord.Frets[3] == 3 && bass.Frets[5] == 0,
    $"TAB draft extractor groups a two-digit fret and chord: {string.Join("; ", candidates.SelectMany(row => row.Columns)
        .Select(column => string.Join(",", column.Frets)))}; digits: {string.Join("; ", draftImage.TabDigits ?? [])}");
Check(TabPdfDraft.Detect(full).Length == 0 && TabPdfDraft.Detect(draftImage with { TabDigits = [] }).Length == 0,
    "TAB detection is opt-in and doesn't guess when readable digits are absent");
Check(TabPdfRhythm.Read(draftImage, candidates[0]).Beats.All(length => length is null),
    "Fret positions alone don't imply a tempo or note length");
var cleanPage = new TabPdfScan.Page(1, 1, draftImage.PdfSha256!, TabPdfDraft.Scan(draftImage),
    [new([.5f, .5f], [0, 0], false)], new(120, 0, "40 45 50 55 59 64"), false);
Check(new TabPdfScan.Score([cleanPage]).PreviewProblem is null &&
    new TabPdfScan.Score([cleanPage with { UnresolvedNavigation = true }]).PreviewProblem is not null &&
    new TabPdfScan.Score([cleanPage with { Rhythms = [new([.5f, null], [0, 0], false)] }]).PreviewProblem is not null,
    "Whole-score playback is unlocked only with complete supported timings and no unresolved navigation");
Check(new TabPdfScan.Score([cleanPage with { Detection = new([], 0, 2) }]) is { StandardNotationOnly: true },
    "Five-line notation is not treated as guitar tablature");
Check(TabPdfDraft.DecodeDigit('!', true) == 0 && TabPdfDraft.DecodeDigit('*', true) == 9 &&
    TabPdfDraft.DecodeDigit('+', true) < 0 && TabPdfDraft.DecodeDigit('!', false) < 0,
    "Legacy TAB digits are decoded only in the Guitar Pro music font; muted strings are not guessed");
Check(PianoMelodyTab.Pitch(-2) == 60 && PianoMelodyTab.Pitch(0) == 64 &&
    PianoMelodyTab.Pitch(1) == 66 && PianoMelodyTab.Pitch(8) == 78,
    "Treble pitch uses ledger lines, octaves, and the detected F sharp key signature");
int[] standard = [64, 59, 55, 50, 45, 40];
int[] testMelody = [60, 67, 69, 84, 40];
Check(PianoMelodyTab.Finger(testMelody).Select(f => standard[f.String - 1] + f.Fret).SequenceEqual(testMelody),
    "Melody fingerings preserve pitch and octave in standard tuning");
Reject(() => PianoMelodyTab.Finger([39]), "outside");
Reject(() => PianoMelodyTab.ReadOpening(draftImage), "piano");
var detected = new TabPdfScan.Score([
    cleanPage with { PageCount = 2 },
    cleanPage with { Number = 2, PageCount = 2, Rhythms = [new([.5f, .5f], [1, 0], false)] },
]).BuildPreview();
using var previewAudio = detected.Render();
Check(detected.Rows is [var firstRow, var secondRow] && firstRow.Page == 1 && secondRow.Page == 2 &&
    detected.RowAt(.25) == firstRow && detected.RowAt(.75) is null && detected.RowAt(1.0) == secondRow &&
    previewAudio.Length > 44,
    "A detected multi-page preview plays locally, preserves rests and follows the correct page");

var bookIndex = LessonTablatureIndex.BundledIndexForValidation();
using var bookStream = typeof(LessonTablatureIndex).Assembly.GetManifestResourceStream(LessonTablatureIndex.BundledResource)!;
using var bookJson = JsonDocument.Parse(bookStream);
var bookHash = bookJson.RootElement.GetProperty("pdfSha256").GetString();
var playheads = new Dictionary<int, LessonScorePlayhead.BundledMap>();
foreach (var name in typeof(LessonScorePlayhead).Assembly.GetManifestResourceNames()
    .Where(name => name.StartsWith(LessonScorePlayhead.BundledPrefix, StringComparison.Ordinal)))
{
    Check(name.EndsWith(LessonScorePlayhead.BundledSuffix, StringComparison.Ordinal) &&
        name.Length == LessonScorePlayhead.BundledPrefix.Length + 3 + LessonScorePlayhead.BundledSuffix.Length,
        $"Invalid playhead resource name: {name}");
    var number = name[LessonScorePlayhead.BundledPrefix.Length..^LessonScorePlayhead.BundledSuffix.Length];
    Check(number.All(char.IsAsciiDigit), $"Invalid playhead exercise number: {name}");
    var exercise = int.Parse(number);
    var map = LessonScorePlayhead.ReadBundled(exercise)
        ?? throw new InvalidOperationException($"Missing or out-of-range playhead resource: {name}");
    Check(map.PdfSha256 == bookHash && bookIndex.Find(exercise)?.PdfPage == map.Map.PdfPage,
        $"Exercise {exercise} playhead belongs to the bundled book and indexed page");
    Check(map.Map.Notes[0].Start > map.CueStartSeconds, $"Exercise {exercise} leaves the narration and count-in off the TAB");
    var week = Math.Min(52, (exercise - 1) / 7 + 1);
    var endOfWeek = exercise == (week == 52 ? 365 : week * 7);
    Check(endOfWeek == (map.CueEndSeconds is null), $"Exercise {exercise} uses the correct kind of cue end");
    playheads.Add(exercise, map);
}
Check(playheads.Count == 365, "All 365 reviewed exercise timings are required");
using (var plugin = new PEReader(File.OpenRead(Path.Combine(pluginDirectory, "FFTrueBard.dll"))))
{
    var metadata = plugin.GetMetadataReader();
    var pluginTypes = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle)).ToArray();
    var lessonTypes = pluginTypes.Where(type => metadata.GetString(type.Namespace) == "FFTrueBard.Lessons")
        .Select(type => metadata.GetString(type.Name)).ToHashSet(StringComparer.Ordinal);
    Check(new[] { "TabPracticeFile", "TabPdfScan", "TabPdfGuide", "TabMidiGuide", "PianoMelodyTab" }.All(lessonTypes.Contains),
        "The DLL includes the local TAB library, notation detection, guide synthesis, and piano-melody converter");
    var window = pluginTypes.Single(type => metadata.GetString(type.Namespace) == "FFTrueBard.UI" &&
        metadata.GetString(type.Name) == "ConfigWindow");
    var readerMethods = window.GetMethods().Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
        .ToHashSet(StringComparer.Ordinal);
    Check(new[] { "DrawMyTabs", "DrawMyTabGuideBuilder", "DrawMyTabScoreOverlay", "DrawMyTabMelody" }.All(readerMethods.Contains),
        "The DLL includes the My Tabs reader, editor, synced controls, and melody preview");
    var resources = plugin.GetSectionData(plugin.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
    var verified = new HashSet<string>(StringComparer.Ordinal);
    var assembly = typeof(LessonScorePlayhead).Assembly;
    foreach (var handle in metadata.ManifestResources)
    {
        var resource = metadata.GetManifestResource(handle);
        var name = metadata.GetString(resource.Name);
        if (!name.StartsWith(LessonScorePlayhead.BundledPrefix, StringComparison.Ordinal)) continue;
        Check(resource.Implementation.IsNil && verified.Add(name), $"Expected one embedded resource: {name}");
        using var expected = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Unexpected plugin timing resource: {name}");
        using var bytes = new MemoryStream();
        expected.CopyTo(bytes);
        var reader = resources.GetReader((int)resource.Offset, resources.Length - (int)resource.Offset);
        var size = reader.ReadInt32();
        Check(size == bytes.Length && reader.ReadBytes(size).AsSpan().SequenceEqual(bytes.GetBuffer().AsSpan(0, size)),
            $"Plugin timing resource differs from the validated set: {name}");
    }
    Check(verified.Count == playheads.Count, "The built plugin embeds the complete reviewed timing set");
}
Console.WriteLine($"Validated {playheads.Count} bundled timing maps against the coordinate index.");

if (args.Length > 1)
{
    // Copy only the user's PDF, without the local sidecar or catalog.
    var local = Path.Combine(folder.Path, "book.pdf");
    File.Copy(args[1], local);
    var index = LessonTablatureIndex.LoadForPdf(local);
    Check(index?.Count == 365, "Built-in index recognized the supported PDF by hash");
    for (var exercise = 1; exercise <= 365; exercise++)
    {
        var entry = index!.Find(exercise)!;
        Check(index.FindPrintedAudio(exercise) == entry, "Printed audio uses the fingerprint-matched box");
        var next = exercise < 365 && (exercise % 7 != 0 || exercise == 364) ? index.Find(exercise + 1) : null;
        var image = LessonPdfRenderer.Render(pluginDirectory, local, entry.PdfPage, entry.Crop, entry.Crop, next);
        var hasInk = false;
        for (var i = 0; i < image.Pixels.Length && !hasInk; i += 4)
            hasInk = image.Pixels[i + 1] < 128;
        Check(hasInk, $"Exercise {exercise} is not blank");
        Check(image.AudioSeconds is not null, $"Exercise {exercise} has a readable printed audio cue");
        Check(image.TabStaff is { } staff && staff.Right > staff.Left && staff.Bottom > staff.Top,
            $"Exercise {exercise} has one TAB row for the playhead");
        if (next is not null)
            Check(image.NextAudioSeconds > image.AudioSeconds, $"Exercise {exercise} ends at the next printed cue");
        if (exercise <= 3)
            Check(image.AudioSeconds == new[] { 0, 11, 23 }[exercise - 1], $"Exercise {exercise} has the expected cue");
        var map = playheads[exercise];
        Check(image.AudioSeconds == map.CueStartSeconds && image.NextAudioSeconds == map.CueEndSeconds,
             $"Exercise {exercise} playhead matches the printed cues");
        var cursor = new LessonScoreCursor(image.TabStaff!.Value, map.CueStartSeconds,
             map.PlaybackEndSeconds ?? double.PositiveInfinity, map.Map);
        var first = map.Map.Notes[0];
        Check(cursor.NoteSynced && !cursor.ShowsNoteAt(map.CueStartSeconds) &&
            Math.Abs(cursor.TimeAt(first.X + first.Width * .5) - first.Start) < .000001,
            $"Exercise {exercise} matches the rendered TAB and first fret seeks past the count-in");
    }
    Console.WriteLine("Rendered and located audio markers and TAB rows for all 365 indexed excerpts with no sidecar.");
}

if (Environment.GetEnvironmentVariable("FFTRUEBARD_MY_TABS_TEST_ROOT") is { Length: > 0 } tabRoot)
{
    var files = Directory.GetFiles(tabRoot, "*.pdf");
    Check(files.Length > 0, "The provided My Tabs folder contains PDF files");
    var rendered = 0;
    foreach (var path in files)
    {
        var count = LessonPdfRenderer.Render(pluginDirectory, path, 1, null).PageCount;
        for (var page = 1; page <= count; page++)
        {
            var image = LessonPdfRenderer.Render(pluginDirectory, path, page, null);
            var hasInk = false;
            for (var i = 0; i < image.Pixels.Length && !hasInk; i += 4)
                hasInk = image.Pixels[i + 1] < 128;
            Check(image.PageCount == count && hasInk, $"My Tabs PDF page {page} renders with a stable page count");
            rendered++;
        }
        var report = TabPdfScan.ReadScore(pluginDirectory, path);
        Check(report.Pages.Length == count, "Whole-score detection visits every page without modifying the PDF");
        Console.WriteLine($"Local TAB scan: {Path.GetFileName(path)}: {report.StaffCount} rows, {report.TimedColumns}/{report.ColumnCount} timed. " +
            (report.PreviewProblem ?? "Whole-score preview eligible."));
        if (Environment.GetEnvironmentVariable("FFTRUEBARD_TAB_DIAGNOSTICS") == "1")
        {
            var first = report.Pages[0];
            Console.WriteLine($"Page 1 settings {first.Settings}, opening staff {first.Detection.FirstTabStaff}, geometry " +
                string.Join(", ", report.Pages.Select(p => $"{p.Detection.FiveLineRows}/{p.Detection.SixLineRows}")));
            if (first.Rhythms.FirstOrDefault() is { } r)
                Console.WriteLine("First staff lengths: " + string.Join(",", r.Beats.Select(b => FormattableString.Invariant($"{b:0.###}"))));
        }
        if (Path.GetFileName(path).StartsWith("041296_", StringComparison.Ordinal))
        {
            var first = report.Pages[0];
            Check(first.Settings.Bpm == 180 && first.Rhythms[0].Beats.SequenceEqual<float?>(
                [.5f, .5f, .5f, .5f, .5f, .5f, .5f, .5f, .5f, .5f, 1, 1]) &&
                first.Rhythms[0].RestsBefore.SequenceEqual<float>([0, 0, 0, 0, 0, 0, 0, 0, .5f, .5f, 0, 0]),
                "The vector-only rhythm of Say It's first two bars includes the isolated eighth notes and their rests");
        }
        if (Path.GetFileName(path) == "Yorushika - Hitchcock.pdf")
        {
            var first = report.Pages[0];
            var frets = first.Detection.Staves[0].Columns;
            Check(first.Settings.Bpm == 150 && frets[0].Frets[1] == 6 && frets[0].Frets[5] == 6 &&
                frets[3].Frets[1] == 7 && frets[3].Frets[4] == 8 && frets.Any(c => c.Frets[2] == 10),
                "Legacy fret glyphs: " + string.Join(" / ", frets.Select(c => string.Join(",", c.Frets))));
        }
        if (Path.GetFileName(path) == "Berserk_sacrifice_acoustic_part.pdf")
        {
            var first = report.Pages[0];
            Check(first.Settings.Bpm == 106 && first.Detection.Staves[0].OutlinedFrets &&
                first.Detection.Staves[0].Columns.Select(c => c.Frets[2]).SequenceEqual<int>([4, 4, 4, 3, 4, 4, 3, 4, 6]) &&
                first.Rhythms[0].Beats.SequenceEqual<float?>([.25f, .25f, .25f, .5f, 1, .25f, .25f, .25f, .25f]) &&
                first.Rhythms[0].HasUnsupportedMarks,
                "Outlined TAB detects the first Berserk phrase's frets and under-staff rhythm without claiming unsupported rests");
        }
        if (Path.GetFileName(path).StartsWith("144497_", StringComparison.Ordinal))
            Check(report.Pages[0].Settings is { Bpm: 130, Capo: 7, Tuning: "39 43 50 53 58 62" },
                $"Senbonzakura's vector tempo, capo 7, and tuning with two flats: {report.Pages[0].Settings}");
        if (Path.GetFileName(path).StartsWith("Bee_My_Honey_", StringComparison.Ordinal))
        {
            var image = LessonPdfRenderer.Render(pluginDirectory, path, 1, null, readTabDigits: true);
            var melody = PianoMelodyTab.ReadOpening(image);
            var attacks = melody.Document.Rows.Single().Attacks;
            Check(attacks.All(a => a.Frets.Length == 1), "The piano converter emits one guitar note per attack");
            var verified = false;
            if (Environment.GetEnvironmentVariable("FFTRUEBARD_MELODY_REVIEW") is { Length: > 0 } reviewPath)
            {
                using var review = JsonDocument.Parse(File.ReadAllBytes(reviewPath));
                var expected = review.RootElement;
                var pitches = attacks.Select(a => standard[a.Frets[0].String - 1] + a.Frets[0].Number);
                var lengths = attacks.Select(a => (int)Math.Round((a.End - a.Start) * melody.Bpm / 15));
                int[] Read(string name) => expected.GetProperty(name).EnumerateArray().Select(v => v.GetInt32()).ToArray();
                Check(melody.Measures == expected.GetProperty("measures").GetInt32() &&
                    melody.Bpm == expected.GetProperty("bpm").GetInt32() && pitches.SequenceEqual(Read("pitches")),
                    $"Piano opening mismatch ({melody.Measures} measures): {string.Join(", ", pitches)}");
                Check(lengths.SequenceEqual(Read("lengths")) && Math.Abs(attacks[0].Start -
                    expected.GetProperty("leadingSixteenths").GetInt32() * 15.0 / melody.Bpm) < .00001,
                    $"The opening must preserve the whole-bar rest and printed durations: {string.Join(", ", lengths)}");
                verified = true;
            }
            var pianoGuide = TabPdfGuide.FromDocument(melody.Document);
            using var audio = pianoGuide.Render();
            Check(pianoGuide.RowAt(0) is null && pianoGuide.RowAt(attacks[0].Start) is { Page: 1 } && audio.Length > 44,
                "Piano melody plays in sync with the treble staff and does not play the bass");
            if (Environment.GetEnvironmentVariable("FFTRUEBARD_MELODY_EXPORT") is { Length: > 0 } export)
            {
                Check(verified, "A local reviewed benchmark must match before exporting this example");
                using var file = new FileStream(export, FileMode.CreateNew);
                using var text = new StreamWriter(file, Encoding.UTF8);
                text.Write(melody.Text);
            }
            Console.WriteLine($"{(verified ? "Verified" : "Read")} a {melody.Measures}-measure melody-only arrangement from the piano opening.");
        }
        if (Path.GetFileName(path) == "Bygone Serenity (Tab).pdf")
        {
            var page = LessonPdfRenderer.Render(pluginDirectory, path, 1, null, readTabDigits: true);
            var draft = TabPdfDraft.Detect(page);
            Check(draft.Length > 0 && draft[0].Columns.Length == 20 &&
                draft[0].Columns[0].Frets[3] == 4 && draft[0].Columns[2].Frets[2] == 2 &&
                draft[0].Columns[2].Frets[3] == 0 && draft[0].Columns[^1].Frets[2] == 2,
                $"The opening Bygone Serenity staff matches the benchmark: {string.Join(", ", draft.Select(row => row.Columns.Length))}");
            var rhythm = TabPdfRhythm.Read(page, draft[0]);
            var settings = TabPdfRhythm.ReadSettings(page, draft[0].Staff.Top);
            Check(settings is { Bpm: 72, Capo: 1, Tuning: "38 45 50 55 59 64" },
                $"Bygone Serenity's printed tempo, capo, and tuning are read: {settings}");
            Check(rhythm.HasUnsupportedMarks, "Repeat signs remain explicitly flagged in an automatically timed draft");
            var reviewedPath = Path.ChangeExtension(path, ".tab-guide.json");
            if (File.Exists(reviewedPath))
            {
                using var reviewed = JsonDocument.Parse(File.ReadAllBytes(reviewedPath));
                var root = reviewed.RootElement;
                if (root.GetProperty("coverage").GetString()?.Contains("opening pickup and measures 1–3", StringComparison.Ordinal) == true)
                {
                    var opening = root.GetProperty("rows")[0].GetProperty("attacks").EnumerateArray().ToArray();
                    for (var i = 0; i < opening.Length; i++)
                    {
                        var frets = Enumerable.Repeat(-1, 6).ToArray();
                        foreach (var fret in opening[i].GetProperty("frets").EnumerateArray())
                            frets[fret.GetProperty("string").GetInt32() - 1] = fret.GetProperty("number").GetInt32();
                        Check(draft[0].Columns[i].Frets.SequenceEqual(frets), $"Detected frets match reviewed attack {i + 1}");
                        var length = (opening[i].GetProperty("end").GetDouble() - opening[i].GetProperty("start").GetDouble()) * 72 / 60;
                        Check(rhythm.Beats[i] is { } beats && Math.Abs(beats - length) < .001 && rhythm.RestsBefore[i] == 0,
                            $"Detected length at reviewed attack {i + 1}: {rhythm.Beats[i]} (expected {length:0.###})");
                    }
                }
            }
            Console.WriteLine("Read Bygone Serenity's tempo, tuning, capo, and 20 opening attacks; checked rhythm against the local review when available.");
        }
    }
    Console.WriteLine($"Rendered all {rendered} pages across {files.Length} local My Tabs PDFs.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CheckPixel(LessonPdfRenderer.Image image, double x, double y, byte b, byte g, byte r)
{
    var offset = ((int)(y * image.Height) * image.Width + (int)(x * image.Width)) * 4;
    Check(image.Pixels.AsSpan(offset, 4).SequenceEqual<byte>([b, g, r, 255]),
        $"Unexpected BGRA pixel at ({x}, {y}).");
}

static void Reject(Action render, string expectedMessage)
{
    try { render(); }
    catch (InvalidDataException e) when (e.Message.Contains(expectedMessage, StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException($"Expected PDF error containing '{expectedMessage}'.");
}

static void WriteTestPdf(string path, string? pageContent = null)
{
    // Red in the top-left, blue in the bottom-right; PDF's origin is bottom-left.
    var content = pageContent ?? ("1 0 0 rg 40 130 200 100 re f\n0 0 1 rg 240 30 200 100 re f\n" +
        "0 0 0 rg BT /F1 7 Tf 42 35 Td (1:11) Tj ET\n");
    string[] objects =
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /CropBox [40 30 440 230] " +
        "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ];
    using var output = File.Create(path);
    void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
    Write("%PDF-1.4\n");
    var offsets = new List<long>();
    foreach (var item in objects)
    {
        offsets.Add(output.Position);
        Write($"{offsets.Count} 0 obj\n{item}\nendobj\n");
    }
    var xref = output.Position;
    Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
    Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
}

sealed class HostMarshaller : ComWrappers
{
    protected override unsafe ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count) =>
        throw new NotSupportedException();
    protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) =>
        throw new NotSupportedException();
    protected override void ReleaseObjects(System.Collections.IEnumerable objects) =>
        throw new NotSupportedException();
}

sealed class ScratchDirectory : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("fftruebard-pdf-");
    public string Path => directory.FullName;
    public void Dispose() => directory.Delete(recursive: true);
}
