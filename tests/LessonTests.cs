using System.Security.Cryptography;
using System.Text.Json;
using FFTrueBard.Lessons;
using NAudio.Wave;

internal static class LessonTests
{
    public static void Run(Action<bool, string> check)
    {
        var weekly = LessonLibrary.ParseTrack("01_Week-01_Exercises-1–7.mp3");
        check(weekly.Week == 1 && weekly.First == 1 && weekly.Last == 7, "Weekly Unicode-dash track ranges parsed");
        var last = LessonLibrary.ParseTrack("52_Week-52_Exercises-358-365.mp3");
        check(last.Week == 52 && last.Last == 365, "Last week includes exercise 365; ASCII dash supported");
        check(LessonLibrary.ParseTrack("Week-99_Exercises-9–1.mp3").Week == 0, "Invalid weekly metadata not treated as a lesson range");
        var rhythm = LessonLibrary.ParseTrack("01-RockBlues-Rhythm_EvenEighthNotes-40bpm.mp3");
        check(rhythm.Bpm == 40 && rhythm.Style == "RockBlues-Rhythm EvenEighthNotes", "Style and BPM read from rhythm filename");
        check(LessonLibrary.ParseTrack("53_Tuning-Notes.mp3").Week == 0, "Tuning track remains available without a lesson range");
        check(LessonLibrary.ParseTrack("My practice recording.mp3").Title == "My practice recording", "Unknown MP3 filenames remain browseable");
        check(LessonLibrary.Scan(new("", "", "", "")).Warnings.Count > 0, "Empty locations guide the user without assuming a machine path");
        foreach (var path in new[] { "relative/folder", "https://example.com/course.mp3", @"\\server\share\lesson.mp3" })
        {
            var refused = false;
            try { LessonLibrary.Normalize(path); }
            catch (ArgumentException) { refused = true; }
            check(refused, "Lesson locations reject relative paths, web URLs, and network shares");
        }

        var directory = Path.Combine(Path.GetTempPath(), "fftruebard-lessons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var exerciseFolder = Path.Combine(directory, "Course_Exercises");
            var rhythmFolder = Path.Combine(directory, "Course_Rhythm");
            Directory.CreateDirectory(exerciseFolder);
            Directory.CreateDirectory(rhythmFolder);
            Directory.CreateDirectory(Path.Combine(exerciseFolder, "nested"));
            File.WriteAllText(Path.Combine(exerciseFolder, "01_Week-01_Exercises-1–7.mp3"), "");
            File.WriteAllText(Path.Combine(exerciseFolder, "nested", "unlisted.mp3"), "");
            File.WriteAllText(Path.Combine(exerciseFolder, "not-a-track.txt"), "");
            File.WriteAllText(Path.Combine(rhythmFolder, "01-Funk-Rhythm-56bpm.MP3"), "");
            File.WriteAllText(Path.Combine(directory, "lesson.pdf"), "");
            var library = LessonLibrary.Scan(new(directory, "", "", ""));
            check(library.Exercises.Count == 1 && library.Rhythm.Count == 1, "Only direct MP3 files scanned; no recursive media copies");
            check(library.Paths.Pdf == Path.Combine(directory, "lesson.pdf"), "Single PDF auto-discovered under selected root");
            check(library.Warnings.Count == 0, "Recognised course folders resolve without warnings");
            File.WriteAllText(Path.Combine(directory, "second.pdf"), "");
            var ambiguous = LessonLibrary.Scan(new(directory, "", "", ""));
            check(ambiguous.Paths.Pdf == "" && ambiguous.Warnings.Any(w => w.Contains("Several PDFs")), "Multiple PDFs require explicit choice");
            var explicitPaths = LessonLibrary.Scan(new("", Path.Combine(directory, "second.pdf"), exerciseFolder, rhythmFolder));
            check(explicitPaths.Exercises.Count == 1 && explicitPaths.Paths.Pdf.EndsWith("second.pdf"), "Separate local folders and PDF work without a root");
            var missing = LessonLibrary.Scan(new("", Path.Combine(directory, "missing.pdf"), Path.Combine(directory, "missing"), ""));
            check(missing.Warnings.Count == 2, "Moved/missing PDF and audio directories reported");
            var missingRoot = false;
            try { LessonLibrary.Scan(new(Path.Combine(directory, "absent"), "", "", "")); }
            catch (DirectoryNotFoundException) { missingRoot = true; }
            check(missingRoot, "Missing lesson root produces actionable failure");
            check(LessonLibrary.Normalize('"' + directory + '"') == directory, "Pasted quoted paths normalised");

            var progressPath = Path.Combine(directory, "state", "lesson-progress.json");
            var progress = new LessonProgress(progressPath);
            progress.Load();
            check(progress.Completed == 0 && !progress.Get(1).Completed, "New progress starts empty");
            check(progress.Save(1, true, "Repeat slowly"), "Lesson progress saves");
            check(progress.Save(365, false, new string('n', 2100)), "Last exercise accepted");
            var reloaded = new LessonProgress(progressPath);
            reloaded.Load();
            check(reloaded.Completed == 1 && reloaded.Get(1).Notes == "Repeat slowly", "Completion and personal notes survive reload");
            check(reloaded.Get(365).Notes.Length == 2000, "Personal notes bounded per exercise");
            check(!reloaded.Save(0, true, "") && !reloaded.Save(366, true, ""), "Out-of-range lessons cannot be saved");
            check(File.Exists(progressPath + ".bak"), "Previous progress preserved before replacement");
            File.WriteAllText(progressPath, "unreadable existing progress");
            var corrupt = new LessonProgress(progressPath);
            corrupt.Load();
            check(corrupt.Status.Contains("original file kept") && File.ReadAllText(progressPath).StartsWith("unreadable"), "Unreadable progress not overwritten on load");
            corrupt.Save(2, true, "New session");
            check(File.ReadAllText(progressPath + ".bak").StartsWith("unreadable"), "Explicit new save backs up unreadable progress");
            var progressJson = File.ReadAllText(progressPath);
            check(!progressJson.Contains(directory) && JsonSerializer.Deserialize<LessonProgressData>(progressJson)!.SchemaVersion == 1,
                "Lesson progress contains no media paths or course content");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var folder in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(folder);
            Directory.Delete(directory);
        }

        var format = new WaveFormat(1000, 16, 2);
        var data = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
        using (var source = new RawSourceWaveStream(new MemoryStream(data), format))
        using (var loop = new LoopingLessonStream(source))
        {
            check(loop.TotalTime.TotalSeconds == 1, "Decoded stream duration available for seeking");
            check(!loop.SetLoop(0.2, 0.2, true) && !loop.SetLoop(-1, 0.5, true) && !loop.SetLoop(0, 2, true), "Invalid/empty loop ranges rejected");
            check(!loop.SetLoop(double.NaN, 1, true), "Non-finite loop points rejected");
            check(loop.SetLoop(0.2, 0.4, true) && loop.Position == 800, "Enabling loop seeks into aligned A-B range");
            var buffer = new byte[2000];
            check(loop.Read(buffer, 0, buffer.Length) == 2000, "Loop fills an audio buffer across multiple wraps");
            check(buffer.Select((b, i) => b == data[800 + i % 800]).All(v => v), "A-B loop uses exact decoded byte boundaries, independent of UI ticks");
            loop.SeekSeconds(0.9);
            check(loop.Read(buffer, 0, 4) == 4 && buffer[0] == data[800], "Seek outside an enabled loop resumes at A");
            loop.SetLoop(0, 1, false);
            loop.SeekSeconds(0.9995);
            check(loop.Position % format.BlockAlign == 0, "Seeking preserves interleaved sample-frame alignment");
            loop.SeekSeconds(0);
            check(loop.Read(buffer, 0, 2000) == 2000 && buffer.SequenceEqual(data.Take(2000)), "Non-loop playback preserves source samples");
            loop.SeekSeconds(1);
            check(loop.Read(buffer, 0, 2000) == 0, "Non-loop playback ends at EOF without spinning");
            loop.SeekSeconds(-100);
            check(loop.Position == 0, "Seek clamps to track start");
            check(loop.SetPlaybackClip(.2, .4) && loop.Position == 800 && !loop.LoopEnabled, "Playing one exercise seeks to its start");
            check(loop.Read(buffer, 0, buffer.Length) == 800 && buffer.Take(800).SequenceEqual(data.Skip(800).Take(800)),
                "One-exercise playback stops before the next exercise samples");
            check(loop.Read(buffer, 0, buffer.Length) == 0, "One-exercise playback does not loop or continue into the next cue");
            loop.SeekSeconds(0);
            check(loop.Position == 800, "Seeking respects the active exercise clip");
            var (measuredStart, measuredEnd) = new LessonAudioCue("", .2, .463).Within(1);
            check(loop.SetPlaybackClip(measuredStart, measuredEnd) && loop.ClipEnd == .463,
                "A reviewed fractional end survives cue validation and reaches the decoded stream");
            loop.SeekSeconds(.438);
            check(loop.Read(buffer, 0, buffer.Length) == 100 &&
                  buffer.Take(100).SequenceEqual(data.Skip(1752).Take(100)) &&
                  loop.Read(buffer, 0, buffer.Length) == 0,
                "A seek after the printed cutoff still plays to the exact measured boundary");
            check(loop.SetClipLoop(.313, .438) && loop.LoopEnabled && loop.ClipEnd == .463,
                "A TAB loop keeps the original exercise clip");
            check(!loop.SetClipLoop(.438, .313) && !loop.SetClipLoop(.311, .405) &&
                  !loop.SetClipLoop(.15, .32) && !loop.SetClipLoop(.338, .49) &&
                  loop.LoopA == .313 && loop.LoopB == .438,
                "Invalid TAB loops leave the existing loop unchanged");
            loop.SeekSeconds(.313);
            check(loop.Read(buffer, 0, buffer.Length) == buffer.Length &&
                  buffer.Select((b, i) => b == data[1252 + i % 500]).All(v => v),
                "A TAB loop plays exactly from A through the last sample before B");
            check(loop.ClearClipLoop() && !loop.LoopEnabled && loop.ClipStart == .2 && loop.ClipEnd == .463,
                "Turning off the TAB loop restores the one-shot exercise range");
            loop.SeekSeconds(.438);
            check(loop.Read(buffer, 0, buffer.Length) == 100 && loop.Read(buffer, 0, buffer.Length) == 0,
                "Reader playback still stops at the measured exercise end after turning off a TAB loop");
            check(loop.SetLoop(.438, .588, true) && loop.ClipEnd is null && loop.LoopEnabled,
                "Explicit A-B playback still replaces the one-shot exercise clip");
            check(!loop.SetClipLoop(.338, .49) && !loop.ClearClipLoop() && loop.LoopEnabled,
                "TAB loop controls do not replace or clear a full-track loop");
            check(loop.SetLoop(0, 1, false) && loop.ClipEnd is null, "Manual audio controls can restore full-track playback");
        }

        CheckLessonTempo(check);

        static bool Near(double a, double b) => Math.Abs(a - b) < .000001;
        var scoreRow = new LessonTabStaff(.1, .4, .9, .5);
        var mapFixture = new LessonScorePlayhead(1, 1, 1, true, "", [
            new(3, 4, .2, .4, .02, .03), new(5, 6, .6, .4, .02, .03),
        ]);
        var scoreCursor = new LessonScoreCursor(scoreRow, 2, 10, mapFixture);
        check(Near(scoreCursor.XAt(3), .21) && Near(scoreCursor.TimeAt(.61), 5),
            "The cursor crosses the reviewed fret at its onset and dragging back to that fret seeks to the onset");
        check(!scoreCursor.ShowsNoteAt(2.99) && scoreCursor.ShowsNoteAt(3) &&
            scoreCursor.ShowsNoteAt(5.99) && !scoreCursor.ShowsNoteAt(6),
            "The TAB playhead appears only during the reviewed phrase, not the count-in or trailing audio");
        check(Near(scoreCursor.TimeAt(.15), 3) && Near(scoreCursor.TimeAt(.21), 3) &&
            Near(scoreCursor.TimeAt(.619), 5) && Near(scoreCursor.TimeAt(.9), 5),
            "Seeking on the score starts at the first note, snaps printed frets to their onsets, and skips the audio tail");
        check(Near(scoreCursor.TimeAt(scoreCursor.XAt(4)), 4) && Near(scoreCursor.XAt(5.9), .61),
            "Dragging between notes interpolates only inside the phrase and the cursor holds on the last note");
        var withoutMap = new LessonScoreCursor(scoreRow, 2, 10, null);
        check(!withoutMap.NoteSynced && !withoutMap.ShowsNoteAt(6) &&
            Near(withoutMap.TimeAt(withoutMap.XAt(6)), 6) && Near(withoutMap.TimeAt(2), 10) &&
            !LessonScoreCursor.MatchesStaff(scoreRow, mapFixture with { Notes = [mapFixture.Notes[1], mapFixture.Notes[0]] }, 2, 10),
            "Unreviewed or invalid maps fall back to bounded audio-time seeking");
        var repeats = mapFixture with { SchemaVersion = 2, Notes = [
            new(3, 4, .2, .4, .02, .03), new(4, 4.5, .4, .4, .02, .03),
            new(5, 6, .2, .4, .02, .03, 1), new(6, 7, .4, .4, .02, .03, 1),
            new(7, 8, .6, .4, .02, .03, 2), new(8, 9, .8, .4, .02, .03, 2),
        ] };
        var repeatCursor = new LessonScoreCursor(scoreRow, 2, 10, repeats);
        check(repeatCursor.NoteSynced && Near(repeatCursor.XAt(4.49), .41) &&
              !repeatCursor.ShowsNoteAt(4.75) && repeatCursor.ShowsNoteAt(5) &&
              Near(repeatCursor.XAt(5), .21) && Near(repeatCursor.XAt(7), .61),
            "A repeat jumps back at the next sounding onset without interpolating across passes or a silent gap");
        check(Near(repeatCursor.TimeAt(.211, 2.5), 3) && Near(repeatCursor.TimeAt(.211, 6), 5) &&
              Near(repeatCursor.TimeAt(.71, 3.5), 7.5) && Near(repeatCursor.TimeAt(.21, 8.5), 5),
            "Seeking chooses the current occurrence of a printed fret, otherwise the nearest pass for that section");
        check(repeatCursor.NoteAtX(.21, 3.5) is { Pass: 0, Start: 3, End: 4 } &&
              repeatCursor.NoteAtX(.21, 5.5) is { Pass: 1, Start: 5, End: 6 } &&
              repeatCursor.NoteAtX(.41, 5.5) is { Pass: 1, Start: 6, End: 7 } &&
              repeatCursor.NoteAtX(.35, 6.5)?.Start == 6 &&
              withoutMap.NoteAtX(.21, 3.5) is null,
            "TAB loop picks snap to the selected note's start and end on the current pass and require a reviewed map");
        check(!LessonScoreCursor.MatchesStaff(scoreRow, repeats with { SchemaVersion = 1 }, 2, 10) &&
              !LessonScoreCursor.MatchesStaff(scoreRow, repeats with { Notes = [repeats.Notes[0], repeats.Notes[4]] }, 2, 10) &&
              !LessonScoreCursor.MatchesStaff(scoreRow, repeats, 2, 8.5),
            "Repeat maps cannot reorder a v1 map, skip pass numbers, or extend outside the decoded clip");
        var bundled = LessonScorePlayhead.ReadBundled(1)!;
        check(bundled.Map is { Exercise: 1, PdfPage: 7, Reviewed: true, Notes.Length: 32 } &&
            Near(bundled.Map.Notes[0].Start, 4.02),
            "The built-in Exercise 1 map contains only the supported timing/position schema and starts after the count-in");
        check(LessonScorePlayhead.ReadBundled(193) is { CueEndSeconds: 49, MeasuredEndSeconds: 49.68 } measured &&
              Near(measured.Map.Notes[^1].Start, 49.175) && measured.Map.Notes[^1].End < measured.MeasuredEndSeconds,
            "Exercise 193 retains its final note past the printed cutoff and separates cursor end from playback end");
        check(LessonScorePlayhead.ReadBundled(294) is { Map.SchemaVersion: 2, Map.Notes.Length: 26 } compound &&
              compound.Map.Notes.Take(15).All(note => note.Pass == 0) &&
              compound.Map.Notes.Skip(15).All(note => note.Pass == 1) &&
              Near(compound.Map.Notes[4].X, compound.Map.Notes[15].X) &&
              compound.Map.Notes[3].X < compound.Map.Notes[15].X,
            "Exercise 294 repeats the two-bar phrase, not its opening pickup");
        CheckMeasuredLessonEnd(check);

        // Optional, read-only integration check against a user-selected local library.
        var local = Environment.GetEnvironmentVariable("FFTRUEBARD_LESSON_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(local))
        {
            var library = LessonLibrary.Scan(new(local, "", "", ""));
            check(library.Exercises.Count > 0 && library.Rhythm.Count > 0 && File.Exists(library.Paths.Pdf), "Local lesson library discovered");
            foreach (var track in library.Exercises.Concat(library.Rhythm))
            {
                using var reader = new AudioFileReader(track.Path);
                var samples = new float[4096];
                check(reader.TotalTime.TotalSeconds > 0 && reader.Read(samples, 0, samples.Length) > 0, "Local MP3 decodes without an external player");
            }
            check(Enumerable.Range(1, 365).All(e => library.Exercises.Count(t => e >= t.First && e <= t.Last) == 1), "Weekly track ranges cover exercises 1–365 exactly once");
            var bundledRoot = Directory.CreateTempSubdirectory("fftruebard-bundled-playhead-");
            try
            {
                var pdf = Path.Combine(bundledRoot.FullName, "book.pdf");
                File.Copy(library.Paths.Pdf, pdf);
                foreach (var exercise in Enumerable.Range(1, 365))
                {
                    var map = LessonScorePlayhead.ReadBundled(exercise)
                        ?? throw new InvalidOperationException($"Missing reviewed playhead for Exercise {exercise}.");
                    var track = library.FindWeeklyExerciseTrack(exercise)!;
                    var copy = Path.Combine(bundledRoot.FullName, Path.GetFileName(track.Path));
                    if (!File.Exists(copy)) File.Copy(track.Path, copy);
                    var result = LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, exercise, map.Map.PdfPage,
                        copy, map.CueStartSeconds, map.CueEndSeconds);
                    check(result.Map?.Notes.SequenceEqual(map.Map.Notes) == true && result.Problem is null &&
                          result.MeasuredEndSeconds == map.MeasuredEndSeconds,
                        $"Exercise {exercise} sync works with only the supported PDF and its weekly MP3");
                }
                var mp3 = Path.Combine(bundledRoot.FullName, Path.GetFileName(library.FindWeeklyExerciseTrack(1)!.Path));
                var week2 = Path.Combine(bundledRoot.FullName, Path.GetFileName(library.FindWeeklyExerciseTrack(8)!.Path));
                var wrong = Path.Combine(bundledRoot.FullName, "other.pdf");
                using (File.Create(wrong)) { }
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 8, 9, mp3, 0, 12).Map is null &&
                    LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, week2, 0, 11).Map is null &&
                    LessonScorePlayhead.FindForReader(bundledRoot.FullName, wrong, 1, 7, mp3, 0, 11).Map is null,
                    "The bundled map fails closed when either media fingerprint differs");
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 2, 7, mp3, 10, 23).Map is null &&
                    LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 2, 7, mp3, 11, 24).Map is null &&
                    LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 2, 9, mp3, 11, 23).Map is null,
                    "A built-in map cannot attach to a different cue or page, even when its note times fit the clip");
                var overridePath = LessonScorePlayhead.DefaultPath(bundledRoot.FullName, pdf, 1);
                Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
                var notes = bundled.Map.Notes.ToArray();
                notes[0] = notes[0] with { Start = 4.03 };
                File.WriteAllText(overridePath, System.Text.Json.JsonSerializer.Serialize(bundled.Map with { Notes = notes }));
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, mp3, 0, 11).Map?.Notes[0].Start == 4.03,
                    "A valid local playhead takes precedence over the built-in Exercise 1 map");
                File.WriteAllText(overridePath, "{}");
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, mp3, 0, 11).Map?.Notes[0].Start == 4.02,
                    "An invalid local playhead does not suppress the matching built-in map");
            }
            finally { bundledRoot.Delete(recursive: true); }
            var playheadPath = LessonScorePlayhead.DefaultPath(library.Paths.Root, library.Paths.Pdf, 1);
            if (File.Exists(playheadPath))
            {
                var track = library.FindWeeklyExerciseTrack(1)!;
                var map = LessonScorePlayhead.TryLoad(playheadPath, 1, 7, track.Path, 0, 11);
                check(map?.Notes.Length == 32 && map.At(0) is null && map.At(4.03) == map.Notes[0] && map.At(11) is null,
                    "Local Exercise 1 map follows reviewed notes without highlighting during the count-in or after the phrase");
                check(LessonScorePlayhead.TryLoad(playheadPath, 1, 7, library.FindWeeklyExerciseTrack(8)!.Path, 0, 11) is null,
                    "The playhead is disabled if the audio file does not match");
                check(LessonScorePlayhead.FindLocal(library.Paths.Exercises, library.Paths.Pdf, 1, 7, track.Path, 0, 11)
                    .Map?.Notes.Length == 32, "The map beside the PDF or weekly audio is found even with a different configured root");
            }
            Console.WriteLine($"Local media check: {library.Exercises.Count} exercise/tuning and {library.Rhythm.Count} rhythm tracks opened read-only.");
        }
    }

    private static void CheckMeasuredLessonEnd(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("fftruebard-measured-cue-");
        try
        {
            string FileWithText(string name, string content)
            {
                var path = Path.Combine(directory.FullName, name);
                File.WriteAllText(path, content);
                return path;
            }
            static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            const int exercise = 200;
            var pdf = FileWithText("book.pdf", "PDF fingerprint fixture");
            var track = FileWithText("weekly.mp3", "MP3 fingerprint fixture");
            var otherPdf = FileWithText("other.pdf", "different PDF");
            var otherTrack = FileWithText("other.mp3", "different MP3");
            var map = new LessonScorePlayhead(1, exercise, 3, true, Hash(track), [
                new(3.2, 5, .2, .4, .02, .03), new(5.05, 5.37, .6, .4, .02, .03),
            ]);
            var envelope = new LessonScorePlayhead.BundledMap(Hash(pdf), 2, 5, map, 5.45);
            var path = LessonScorePlayhead.DefaultPath(directory.FullName, pdf, exercise);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            void Write(LessonScorePlayhead.BundledMap value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
            LessonScorePlayhead.ReaderTiming Find(int? printed = 5, string? pdfPath = null, string? trackPath = null, int page = 3)
                => LessonScorePlayhead.FindForReader(directory.FullName, pdfPath ?? pdf, exercise, page, trackPath ?? track, 2, printed);

            Write(envelope);
            var match = Find();
            check(match.MeasuredEndSeconds == 5.45 && match.Map?.Notes[^1].Start == 5.05,
                "A measured fractional endpoint with matching media retains a final note after the printed cutoff");
            var cursor = new LessonScoreCursor(new(.1, .4, .9, .5), 2, match.MeasuredEndSeconds!.Value, match.Map);
            check(cursor.NoteSynced && cursor.TimeAt(.61) == 5.05 && cursor.ShowsNoteAt(5.32) && !cursor.ShowsNoteAt(5.39),
                "The late fret can be sought and the playhead hides before the measured audio endpoint");
            check(Find(6).Map is null && Find(pdfPath: otherPdf).Map is null && Find(trackPath: otherTrack).Map is null &&
                  Find(page: 4).Map is null, "Neither a measured end nor its notes may attach to different media, printed cues, or page");
            foreach (var invalid in new[] {
                envelope with { MeasuredEndSeconds = null },
                envelope with { MeasuredEndSeconds = 4.99 },
                envelope with { MeasuredEndSeconds = 5.25 },
                envelope with { MeasuredEndSeconds = 7.05 },
                envelope with { CueEndSeconds = null },
                envelope with { Map = map with { Reviewed = false } },
            })
            {
                Write(invalid);
                var actual = Find(invalid.CueEndSeconds);
                check(actual.Map is null && actual.MeasuredEndSeconds is null,
                    "Unreviewed, unbounded, or inconsistent endpoint overrides fail closed");
            }
            File.WriteAllText(path, JsonSerializer.Serialize(map with { Notes = [map.Notes[0]] }));
            var legacy = Find();
            check(legacy.Map?.Notes.Length == 1 && legacy.MeasuredEndSeconds is null,
                "Legacy local playheads still load and keep the printed cutoff");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void CheckLessonTempo(Action<bool, string> check)
    {
        const int sampleRate = 24000;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        var samples = new float[sampleRate * 3 * format.Channels];
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            for (var channel = 0; channel < 2; channel++)
                samples[2 * frame + channel] = frame >= sampleRate && frame < 2 * sampleRate
                    ? .5f * MathF.Sin(2 * MathF.PI * (channel == 0 ? 440 : 660) * frame / sampleRate)
                    : 4f;
        }
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var source = new RawSourceWaveStream(new MemoryStream(bytes), format);
        using var clip = new LoopingLessonStream(source);
        clip.SetPlaybackClip(1, 2);
        var player = new LessonTempoProvider(clip);
        var buffer = new byte[8192 + 16];
        var output = new float[buffer.Length / sizeof(float)];
        float[] ReadAll()
        {
            var result = new List<float>();
            for (var i = 0; i < 100; i++)
            {
                var count = player.Read(buffer, 16, 8192);
                if (count == 0) return result.ToArray();
                Buffer.BlockCopy(buffer, 16, output, 0, count);
                result.AddRange(output.AsSpan(0, count / sizeof(float)).ToArray());
            }
            throw new InvalidOperationException("Stretched exercise did not stop.");
        }

        foreach (var speed in new[] { 50, 75, 100, 125, 150 })
        {
            player.Reset(1, speed);
            var stretched = ReadAll();
            var duration = stretched.Length / (double)(2 * sampleRate);
            check(Math.Abs(duration - 100d / speed) < .01 && stretched.All(s => Math.Abs(s) < .6),
                $"At {speed}% playback duration scales and audio from either side of the clip stays out");
            if (speed == 100)
                check(stretched.AsSpan().SequenceEqual(samples.AsSpan(2 * sampleRate, 2 * sampleRate)),
                    "Normal speed sends the original decoded samples without stretching");
            var start = sampleRate / 5;
            var end = 2 * sampleRate / 5;
            for (var channel = 0; channel < 2; channel++)
            {
                var crossings = 0;
                for (var frame = start; frame < end; frame++)
                    if (stretched[2 * (frame - 1) + channel] <= 0 && stretched[2 * frame + channel] > 0) crossings++;
                check(Math.Abs(crossings * 5 - (channel == 0 ? 440 : 660)) <= 15,
                    $"At {speed}% channel {channel + 1} preserves pitch");
            }
        }

        player.Reset(1, 50);
        player.Read(buffer, 16, 8192);
        player.Reset(1.9, 125);
        var tail = ReadAll();
        check(Math.Abs(tail.Length / (double)(2 * sampleRate) - .08) < .005 && tail.Any(s => Math.Abs(s) > .1),
            "Seeking and changing speed discards buffered data and finishes a short clip tail");

        check(clip.SetClipLoop(1.25, 1.875), "A TAB loop can be enabled inside a clip with pitch-preserving playback");
        player.Reset(1.8, 75);
        check(Enumerable.Range(0, 25).All(_ => player.Read(buffer, 16, 8192) == 8192) &&
            clip.Seconds >= 1.25 && clip.Seconds < 1.875, "Stretched playback loops without reading outside the TAB A–B range");
        check(Math.Abs(LessonTempoProvider.SourceTime(4, 2, 50, 7, false, 0, 0) - 5) < 1e-6 &&
            Math.Abs(LessonTempoProvider.SourceTime(4, 2, 150, 6.8, false, 0, 0) - 6.8) < 1e-6 &&
            Math.Abs(LessonTempoProvider.SourceTime(1.5, 3, 150, 1.8, true, 1, 2) - 1) < 1e-6,
            "Playhead uses original recording seconds, bounded by the decoder or wrapped by the loop");
    }
}
