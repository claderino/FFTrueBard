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
            var playheadPath = LessonScorePlayhead.DefaultPath(library.Paths.Root, library.Paths.Pdf, 1);
            if (File.Exists(playheadPath))
            {
                var track = library.FindWeeklyExerciseTrack(1)!;
                var map = LessonScorePlayhead.TryLoad(playheadPath, 1, 7, track.Path, 0, 11);
                check(map?.Notes.Length == 32 && map.At(0) is null && map.At(4.03) == map.Notes[0] && map.At(11) is null,
                    "Local Exercise 1 map follows reviewed notes without highlighting during the count-in or after the phrase");
                check(LessonScorePlayhead.TryLoad(playheadPath, 1, 7, library.FindWeeklyExerciseTrack(8)!.Path, 0, 11) is null,
                    "The playhead is disabled if the audio file does not match");
            }
            Console.WriteLine($"Local media check: {library.Exercises.Count} exercise/tuning and {library.Rhythm.Count} rhythm tracks opened read-only.");
        }
    }
}
