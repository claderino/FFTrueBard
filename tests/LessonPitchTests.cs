using System.Text.Json;
using FFTrueBard.Detection;
using FFTrueBard.Lessons;

internal static class LessonPitchTests
{
    public static void Run(Action<bool, string> check)
    {
        // Independently chosen targets; no published course transcription in tests.
        var sequence = new LessonSequence(1, 1, "Pitch order test", "Generated test tones",
            [new(69), new(69), new(71)]);
        var settings = new DetectionSettings(0.15f, 0.004f, 2, 150, 100);
        DetectionFrame Pick(long at, int midi = 69, double cents = 0) => new(at,
            440 * Math.Pow(2, (midi - 69 + cents / 100) / 12.0), SequenceNote.NoteName(midi), midi,
            cents, 0.1f, 0.2f, 0.95f, "accepted", false, settings, at - 10);
        DetectionFrame Mute(long at) => new(at, 0, "", -1, 0, 0, 0, 0, "gated", true, settings);
        var drill = new LessonPitchPractice();
        drill.Start(sequence, 1000, 35);
        check(drill.Running && drill.WaitingForMute && drill.Expected!.Midi == 69, "Pitch drill initially requires mute");
        drill.Receive(Pick(1100), 1100);
        check(drill.Correct == 0 && drill.Wrong == 0, "Ringing note at start is not scored");
        drill.Receive(Mute(1200) with { Released = false }, 1200);
        check(drill.WaitingForMute, "Short silence does not rearm pitch drill");
        drill.Receive(Mute(1300), 1300);
        check(!drill.WaitingForMute && drill.State == "ready", "Released silence rearms pitch drill");
        drill.Receive(Pick(1400), 1400);
        check(drill.Correct == 1 && drill.Index == 1 && drill.State == "correct", "Matching pitch advances exactly one target");
        drill.Receive(Pick(1500), 1500);
        drill.Receive(Pick(1500), 1500);
        check(drill.Correct == 1, "Repeated and duplicate frames do not count a ringing note twice");
        drill.Receive(Mute(1600), 1600);
        drill.Receive(Pick(1700, 57), 1700);
        check(drill.Wrong == 1 && drill.Index == 1 && drill.Retries[1] == 1 && drill.State == "wrong", "Wrong octave stays on same expected pitch");
        drill.Receive(Mute(1800), 1800);
        drill.Receive(Pick(1900, 69, 40), 1900);
        check(drill.Wrong == 2 && drill.Correct == 1, "Pitch beyond cents tolerance is a wrong-pitch attempt");
        drill.Receive(Mute(2000), 2000);
        drill.Receive(Pick(2100, 69, -35), 2100);
        check(drill.Correct == 2 && drill.Index == 2, "Pitch tolerance includes the boundary");
        drill.Skip();
        check(!drill.Running && drill.Finished && drill.Skipped == 1 && drill.Results[2] == "skipped", "Explicit skip ends sequence without inventing a detected miss");
        drill.Skip();
        drill.Receive(Pick(2200), 2200);
        check(drill.Skipped == 1 && drill.Wrong == 2, "Finished run ignores additional events");

        foreach (var invalid in new[]
        {
            Pick(1200) with { Confidence = 0.79f },
            Pick(1200) with { Peak = 0.98f },
            Pick(1200) with { Rms = float.NaN },
            Pick(1200) with { Confidence = float.NaN },
            Pick(1200) with { Frequency = double.NaN },
            Pick(1200) with { Cents = double.PositiveInfinity },
            Pick(1200) with { Rms = 0.0001f },
            Pick(1200) with { Midi = -1 },
            Pick(1200) with { Frequency = 0 },
            Pick(1200) with { Released = true },
            Pick(1200) with { OnsetTimestampMs = 1050 },
            Pick(1200) with { OnsetTimestampMs = 1300 },
        })
        {
            drill.Start(sequence, 1000, 35);
            drill.Receive(Mute(1100), 1100);
            drill.Receive(invalid, 1200);
            check(drill.Correct == 0 && drill.Wrong == 0 && drill.State == "uncertain" && drill.WaitingForMute,
                "Unreliable pitch is unscored and requires fresh mute");
        }
        drill.Start(sequence, 1000, 35);
        drill.Receive(Mute(1100), 1100);
        long clock = 1200;
        foreach (var state in new[] { "stabilizing", "unvoiced", "cooldown" })
        {
            drill.Receive(Pick(clock) with { Decision = state }, clock);
            clock += 50;
        }
        check(drill.UncertainFrames == 3 && drill.Correct == 0 && drill.Wrong == 0, "Uncertain frames are counted separately from pitch attempts");
        drill.Receive(Mute(1400), 1400);
        drill.Tick(2800);
        check(drill.Running && drill.Wrong == 0 && drill.Skipped == 0, "Recent silence keeps run alive without a missed-note penalty");
        drill.Tick(3001);
        check(!drill.Running && drill.State == "uncertain" && drill.Wrong == 0, "Stalled capture stops grading without adding mistakes");

        drill.Start(sequence, 1000, 35);
        drill.Receive(Mute(1100), 1100);
        drill.Receive(Pick(1200), 1800);
        check(drill.State == "uncertain" && drill.Correct == 0, "Queued stale accepted frame never scores");
        drill.Start(sequence, 1000, 35);
        drill.Receive(Mute(1100), 1100);
        drill.Receive(Pick(1500), 1200);
        check(drill.State == "uncertain" && drill.Correct == 0, "Future-dated accepted frame never scores");
        drill.Stop("Capture interrupted");
        check(drill.Outcome == "Stopped" && drill.Feedback == "Capture interrupted", "Interruption retains explicit outcome");
        drill.Start(sequence, 4000, float.NaN);
        check(drill.CentsTolerance == 35 && drill.Index == 0 && drill.Wrong == 0, "Restart resets run and sanitises tolerance");
        sequence.Notes[0] = new(72);
        check(drill.Expected!.Midi == 69, "Run owns a snapshot of expected pitches");
        sequence.Notes[0] = new(69);

        check(new SequenceNote(64, 1, 0).Valid && new SequenceNote(40, 6, 0).Valid, "String guide uses sounding standard-tuning pitches");
        foreach (var note in new[] { new SequenceNote(39), new(88), new(64, 0, 0), new(64, 7, 0), new(64, 1), new(64, null, 0), new(65, 1, 0), new(64, 1, -1), new(89, 1, 25) })
            check(!note.Valid, "Invalid MIDI or inconsistent string/fret guide rejected");
        check(SequenceNote.NoteName(64) == "E4" && SequenceNote.NoteName(-1) == "--", "Note names include sounding octave");
        var directory = Path.Combine(Path.GetTempPath(), "fftruebard-pitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.notes.json");
            File.WriteAllText(path, JsonSerializer.Serialize(sequence, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            check(LessonSequence.Load(path, 1).Notes.Length == 3, "Camel-case local sequence loads and validates");
            var rejected = false;
            try { LessonSequence.Load(path, 2); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Wrong exercise file cannot grade another selected exercise");
            foreach (var bad in new[]
            {
                sequence with { SchemaVersion = 2 }, sequence with { Title = "" },
                sequence with { Source = null! }, sequence with { Notes = [] },
                sequence with { Notes = [null!] }, sequence with { Notes = [new(20)] },
                sequence with { Notes = new SequenceNote[513] }, sequence with { Exercise = 0 },
            })
            {
                rejected = false;
                try { bad.Validate(1); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "Invalid local sequence metadata rejected");
            }
            File.WriteAllText(path, new string(' ', 256 * 1024 + 1));
            rejected = false;
            try { LessonSequence.Load(path, 1); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Oversize local note file rejected before parsing");
            check(LessonSequence.DefaultPath(directory, "", 1) == Path.Combine(directory, "fftruebard-lessons", "exercise-001.notes.json"), "Conventional per-exercise file discovery");
            check(LessonSequence.DefaultPath("", Path.Combine(directory, "book.pdf"), 2).EndsWith("exercise-002.notes.json"), "PDF-only setup supports sibling note-data folder");
            check(LessonSequence.DefaultPath("", "", 1) == "", "No implicit media location on a new PC");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }

        // Detector-to-grader integration: real synthesized samples, no fabricated acceptance decisions.
        var processor = new PitchProcessor(48000);
        drill.Start(sequence, 1000, 35);
        long time = 1000;
        foreach (var target in sequence.Notes)
        {
            for (var n = 0; n < 4; n++)
            {
                time += 43;
                drill.Receive(processor.Process(new float[2048], time, settings), time);
            }
            var tone = Enumerable.Range(0, 2048).Select(i => (float)(0.15 * Math.Sin(2 * Math.PI * 440 * Math.Pow(2, (target.Midi - 69) / 12.0) * i / 48000))).ToArray();
            for (var n = 0; n < 5; n++)
            {
                time += 43;
                drill.Receive(processor.Process(tone, time, settings), time);
            }
        }
        check(drill.Finished && drill.Correct == 3 && drill.Wrong == 0, "YIN detector feeds mute-separated repeated pitches through complete drill");

        var localRoot = Environment.GetEnvironmentVariable("FFTRUEBARD_LESSON_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(localRoot))
        {
            var local = LessonSequence.Load(LessonSequence.DefaultPath(localRoot, "", 1), 1);
            check(local.Notes.All(n => n.Valid) && local.Notes.Length > 0, "User-local pilot file validates without publishing its contents");
        }
    }
}
