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
        var bundled = LessonScorePlayhead.ReadBundled();
        check(bundled.Map is { Exercise: 1, PdfPage: 7, Reviewed: true, Notes.Length: 32 } &&
            Near(bundled.Map.Notes[0].Start, 4.02),
            "The built-in Exercise 1 map contains only the supported timing/position schema and starts after the count-in");

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
                var mp3 = Path.Combine(bundledRoot.FullName, "week-1.mp3");
                File.Copy(library.Paths.Pdf, pdf);
                File.Copy(library.FindWeeklyExerciseTrack(1)!.Path, mp3);
                var result = LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, mp3, 0, 11);
                check(result.Map?.Notes.Length == 32 && result.Problem is null, "Exercise 1 sync works with only the supported PDF and MP3");
                var wrong = Path.Combine(bundledRoot.FullName, "other.mp3");
                using (File.Create(wrong)) { }
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, wrong, 0, 11).Map is null &&
                    LessonScorePlayhead.FindForReader(bundledRoot.FullName, wrong, 1, 7, mp3, 0, 11).Map is null,
                    "The bundled map fails closed when either media fingerprint differs");
                var overridePath = LessonScorePlayhead.DefaultPath(bundledRoot.FullName, pdf, 1);
                Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
                var notes = bundled.Map.Notes.ToArray();
                notes[0] = notes[0] with { Start = 4.03 };
                File.WriteAllText(overridePath, System.Text.Json.JsonSerializer.Serialize(bundled.Map with { Notes = notes }));
                check(LessonScorePlayhead.FindForReader(bundledRoot.FullName, pdf, 1, 7, mp3, 0, 11).Map?.Notes[0].Start == 4.03,
                    "A valid local playhead takes precedence over the built-in Exercise 1 map");
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

        clip.SetLoop(1, 2, true);
        player.Reset(1.8, 75);
        check(Enumerable.Range(0, 25).All(_ => player.Read(buffer, 16, 8192) == 8192) &&
            clip.Seconds >= 1 && clip.Seconds < 2, "Stretched playback loops without reading outside the A–B range");
        check(Math.Abs(LessonTempoProvider.SourceTime(4, 2, 50, 7, false, 0, 0) - 5) < 1e-6 &&
            Math.Abs(LessonTempoProvider.SourceTime(4, 2, 150, 6.8, false, 0, 0) - 6.8) < 1e-6 &&
            Math.Abs(LessonTempoProvider.SourceTime(1.5, 3, 150, 1.8, true, 1, 2) - 1) < 1e-6,
            "Playhead uses original recording seconds, bounded by the decoder or wrapped by the loop");
    }
}
