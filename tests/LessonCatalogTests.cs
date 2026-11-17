using System.Text.Json;
using FFTrueBard.Lessons;
using NAudio.Wave;

internal static class LessonCatalogTests
{
    public static void Run(Action<bool, string> check)
    {
        // Independent lesson content; course-derived cards are never committed.
        var card = new LessonExercise
        {
            Exercise = 1, Title = "Controlled practice", Technique = "Picking",
            Steps = ["Read your notation.", "Listen and practise slowly."],
            SelfChecks = ["My own observation."], PdfPage = 5, PrintedPage = "iv",
            MinBpm = 40, MaxBpm = 80, Audio = new("", 2, 8),
        };
        var catalog = new LessonCatalog(1, "Personal course", "Independently authored test content",
            [card, card with { Exercise = 8, Technique = "Legato" }, card with { Exercise = 365 }]);
        catalog.Validate();
        check(card.Mode == "guided" && card.SequencePath(Path.Combine(Path.GetTempPath(), "test.course.json")) == "",
            "Lesson cards default to guided mode with no grading target");
        check(catalog.Filter(1).Single().Exercise == 1, "Catalog week filter follows exercise number");
        check(catalog.Filter(52).Single().Exercise == 365, "Exercise 365 remains in week 52");
        check(catalog.Filter(0, "picking").Count() == 2, "Technique filtering is case insensitive");
        check(!catalog.Filter(4).Any(), "Missing cards are not invented by the catalog");
        foreach (var bad in new[]
        {
            card with { Exercise = 0 }, card with { Exercise = 366 }, card with { Title = "" },
            card with { Title = new string('a', 201) }, card with { Technique = null! },
            card with { Steps = [] }, card with { Steps = [null!] }, card with { Steps = [""] },
            card with { Steps = Enumerable.Repeat("step", 21).ToArray() },
            card with { Steps = [new string('a', 1001)] }, card with { SelfChecks = null! },
            card with { SelfChecks = [new string('a', 501)] }, card with { PdfPage = 0 },
            card with { PdfPage = 10001 }, card with { PrintedPage = new string('a', 41) },
            card with { MinBpm = 80, MaxBpm = 40 }, card with { MinBpm = null },
            card with { MinBpm = 19 }, card with { MaxBpm = 401 },
            card with { Mode = "automatic-chord" }, card with { Mode = null! },
            card with { PitchSequenceFile = "local.notes.json" },
            card with { Mode = "pitch-order", PitchSequenceFile = "../private.notes.json" },
            card with { Mode = "pitch-order", PitchSequenceFile = @"C:\private.notes.json" },
            card with { Mode = "pitch-order", PitchSequenceFile = "https://example.com/test.notes.json" },
            card with { Mode = "pitch-order", PitchSequenceFile = "notation.exe" },
        })
        {
            var rejected = false;
            try { bad.Validate(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed guided card, tempo, grading mode, or file reference rejected");
        }
        foreach (var bad in new[]
        {
            catalog with { SchemaVersion = 2 }, catalog with { Exercises = [] },
            catalog with { Exercises = [card, card] }, catalog with { Exercises = [null!] },
            catalog with { Exercises = Enumerable.Repeat(card, 366).ToArray() },
            catalog with { Source = new string('a', 501) },
        })
        {
            var rejected = false;
            try { bad.Validate(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Bad catalog schema, duplicate IDs, or invalid catalog size rejected");
        }
        var tracks = new LessonLibrary(new("", "", "", ""),
            [new("weekly.mp3", "Weekly audio", 1, 1, 7)], [], []);
        check(card.Audio!.ResolveTrack(tracks, 1).Path == "weekly.mp3", "Blank cue filename resolves exactly one weekly exercise track");
        check(new LessonAudioCue("WEEKLY.MP3", 0).ResolveTrack(tracks, 1).Path == "weekly.mp3", "Explicit MP3 filename resolves without path traversal");
        check(card.Audio.Within(10) == (2, 8), "Cue preserves authored start and end boundaries");
        check(new LessonAudioCue("", 2).Within(10) == (2, 10), "Open-ended cue resolves to actual decoded duration");
        foreach (var invalid in new[]
        {
            new LessonAudioCue("../audio.mp3", 0), new(@"..\audio.mp3", 0), new("https://example.com/a.mp3", 0),
            new("sound.exe", 0), new("audio.mp3:stream.mp3", 0), new(null!, 0),
            new("", double.NaN), new("", -1), new("", double.PositiveInfinity),
            new("", 3, 2), new("", 3, 3), new("", 3, double.NaN), new("", 86401),
        })
        {
            var rejected = false;
            try { invalid.Validate(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed cue or nonlocal MP3 reference rejected");
        }
        foreach (var (cue, duration) in new[]
        {
            (new LessonAudioCue("", 12), 10.0), (new LessonAudioCue("", 10), 10.0),
            (new LessonAudioCue("", 2, 11), 10.0), (new LessonAudioCue("", 0), 0.0),
            (new LessonAudioCue("", 0), double.NaN),
        })
        {
            var rejected = false;
            try { cue.Within(duration); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Cue outside decoded duration never silently clamps");
        }
        foreach (var library in new[] { tracks with { Exercises = [] }, tracks with { Exercises = [tracks.Exercises[0], tracks.Exercises[0]] } })
        {
            var rejected = false;
            try { card.Audio.ResolveTrack(library, 1); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Missing or ambiguous audio matches require user correction");
        }
        using (var source = new RawSourceWaveStream(new MemoryStream(new byte[40000]), new WaveFormat(1000, 16, 2)))
        using (var stream = new LoopingLessonStream(source))
        {
            var cue = card.Audio.Within(stream.TotalTime.TotalSeconds);
            check(stream.SetLoop(cue.Start, cue.End, false), "Catalog range is compatible with decoded audio stream");
            stream.SeekSeconds(cue.Start);
            check(stream.Seconds == 2 && !stream.LoopEnabled, "Cue seeks without enabling looping or autoplay");
        }

        var directory = Path.Combine(Path.GetTempPath(), "fftruebard-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "personal.course.json");
            var original = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(path, original);
            var loaded = LessonCatalog.Load(path);
            check(loaded.Exercises.Length == 3 && loaded.Exercises[0].Steps.Length == 2, "Local card content and checklist load with camel-case keys");
            check(File.ReadAllText(path) == original, "Catalog loading never modifies local source");
            var noteCard = card with { Mode = "pitch-order", PitchSequenceFile = "my-phrase.notes.json" };
            check(noteCard.SequencePath(path) == Path.Combine(directory, "my-phrase.notes.json"), "Note data resolves beside portable catalog file");
            check((noteCard with { PitchSequenceFile = "" }).SequencePath(path) == Path.Combine(directory, "exercise-001.notes.json"), "Pitch card may use default per-exercise filename");
            check(LessonCatalog.DefaultPath(directory, "") == Path.Combine(directory, "fftruebard-lessons", "exercises.course.json"), "Catalog auto-discovery follows saved course root");
            check(LessonCatalog.DefaultPath("", Path.Combine(directory, "book.pdf")).EndsWith("fftruebard-lessons" + Path.DirectorySeparatorChar + "exercises.course.json"), "PDF-only paths support local catalog discovery");
            check(LessonCatalog.DefaultPath("", "") == "", "New PC has no assumed catalog path");
            File.WriteAllText(path, "{ invalid JSON");
            var rejected = false;
            try { LessonCatalog.Load(path); }
            catch (JsonException) { rejected = true; }
            check(rejected, "Malformed JSON never enables a partial catalog");
            File.WriteAllText(path, new string(' ', 2 * 1024 * 1024 + 1));
            rejected = false;
            try { LessonCatalog.Load(path); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Catalog size limit checked before deserialization");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }

        var root = Environment.GetEnvironmentVariable("FFTRUEBARD_LESSON_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var path = LessonCatalog.DefaultPath(root, "");
            if (File.Exists(path))
            {
                var local = LessonCatalog.Load(path);
                var library = LessonLibrary.Scan(new(root, "", "", ""));
                foreach (var exercise in local.Exercises)
                {
                    if (exercise.Mode == "pitch-order")
                        check(LessonSequence.Load(exercise.SequencePath(path), exercise.Exercise).Notes.Length > 0, "Local pitch-enabled card resolves valid reviewed note data");
                    if (exercise.Audio is not { } cue) continue;
                    using var reader = new AudioFileReader(cue.ResolveTrack(library, exercise.Exercise).Path);
                    var range = cue.Within(reader.TotalTime.TotalSeconds);
                    check(range.Start >= 0 && range.End <= reader.TotalTime.TotalSeconds, "User-local cue fits actual MP3 duration");
                }
                Console.WriteLine($"Local catalog check: {local.Exercises.Length} cards validated; media and note references opened read-only.");
            }
        }
    }
}
