using System.Text.Json;
using FFTrueBard.Detection;
using FFTrueBard.Diagnostics;
using FFTrueBard.Input;
using FFTrueBard.Practice;

if (args.Length > 0 && args[0] == "--reference-audio")
{
    if (args.Length != 3)
        throw new ArgumentException("Usage: --reference-audio <absolute .reference.json case> <new absolute output directory>");
    ReferenceAudioEvaluation.RunCase(args[1], args[2]);
    return;
}

// Regression runner: no game installation or audio hardware required.
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
    checks++;
}

float[] Tone(double frequency, int rate = 48000, double amplitude = 0.2, bool harmonics = false)
{
    var samples = new float[(int)(2048L * rate / 48000)];
    for (var i = 0; i < samples.Length; i++)
    {
        var phase = 2 * Math.PI * frequency * i / rate;
        samples[i] = (float)(amplitude * (Math.Sin(phase) +
            (harmonics ? 0.45 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase) : 0)));
    }
    return samples;
}

var settings = new DetectionSettings(0.15f, 0.004f, 2, 150, 100);
foreach (var rate in new[] { 44100, 48000, 96000 })
{
    // Every expected pitch in the chord catalog, plus detector range endpoints.
    var frequencies = Chords.All.SelectMany(chord => Enumerable.Range(0, 6)
        .Where(i => chord.Frets[i] >= 0).Select(i => 440 * Math.Pow(2, (chord.MidiAt(i) - 69) / 12.0)))
        .Concat([73.416, 440, 1234.71]).Distinct();
    foreach (var frequency in frequencies)
    foreach (var harmonics in new[] { false, true })
    {
        var result = YinPitch.Analyze(Tone(frequency, rate, harmonics: harmonics), rate, 70, 1300, 0.15f);
        var errorCents = 1200 * Math.Log2(result.Frequency / frequency);
        Check(Math.Abs(errorCents) < 5, $"Pitch accuracy: {frequency:F2} Hz, {rate}, harmonics={harmonics}, error={errorCents:F2} cents");
        Check(result.Confidence > 0.9, "Periodic tone confidence");
    }
    Check(YinPitch.Detect(new float[2048], rate, 70, 1300, 0.15f) == 0, "Silence has no pitch");
}
Check(YinPitch.Detect(new float[20], 48000, 70, 1300, 0.15f) == 0, "Short frame rejected");

var noise = new float[2048];
var rng = new Random(713);
for (var i = 0; i < noise.Length; i++)
    noise[i] = (float)(rng.NextDouble() - 0.5);
Check(YinPitch.Detect(noise, 48000, 70, 1300, 0.15f) == 0, "White noise rejected");

var processor = new PitchProcessor(48000);
var a = Tone(440);
var silent = new float[2048];
Check(processor.Process(a, 1000, settings).Decision == "stabilizing", "First frame must stabilize");
var accepted = processor.Process(a, 1043, settings);
Check(accepted.Decision == "accepted" && accepted.Note == "A4", "Stable A4 accepted");
Check(Math.Abs(accepted.Cents) < 5, "Cents reported correctly");
Check(processor.Process(a, 3000, settings).Decision == "sustained", "Sustain never auto-repeats");
Check(!processor.Process(silent, 3043, settings).Released, "Brief silence does not rearm");
processor.Process(silent, 3086, settings);
Check(processor.Process(silent, 3129, settings).Released, "Release after quiet duration");
processor.Process(a, 3172, settings);
Check(processor.Process(a, 3215, settings).Decision == "accepted", "Same note after mute accepted");
Check(processor.Process(a, 3300, settings with { RmsGate = 0.5f }).Decision == "gated", "Gate tuning applies immediately");

var cooldown = new PitchProcessor(48000);
var quick = settings with { StableFrames = 1 };
cooldown.Process(a, 1000, quick);
Check(cooldown.Process(Tone(329.63), 1043, quick).Decision == "cooldown", "Minimum interval enforced");
Check(cooldown.Process(Tone(329.63), 1200, quick).Decision == "accepted", "New note accepted after interval");
cooldown.Process(noise, 1300, quick);
Check(cooldown.Process(a, 1500, settings).Decision == "stabilizing", "Unvoiced frame resets stability");

DetectionFrame Heard(int midi, long time, double cents = 0) => new(time,
    440 * Math.Pow(2, (midi - 69 + cents / 100) / 12.0), "test-note", midi, cents,
    0.1f, 0.2f, 0.99f, "accepted", false, settings);
DetectionFrame Mute(long time) => new(time, 0, "", -1, 0, 0, 0, 0, "gated", true, settings);

var practice = new ChordPractice();
practice.Configure(2, 1, false); // C = X 3 2 0 1 0
Check(practice.StringIndex == 1 && practice.Expected.Contains("C3"), "C skips low E and expects C3");
practice.Receive(Heard(48, 1000), 35);
Check(practice.StringIndex == 1, "Must mute before first pick");
practice.Receive(Mute(1100), 35);
practice.Receive(Heard(60, 1200), 35);
Check(practice.Mistakes == 1 && practice.StringIndex == 1, "Wrong octave rejected");
practice.Receive(Mute(1300), 35);
practice.Receive(Heard(48, 1400, 45), 35);
Check(practice.Mistakes == 2, "Out of tune note rejected");
practice.Receive(Mute(1500), 35);
practice.Receive(Heard(48, 1600, -10), 35);
Check(practice.StringIndex == 2, "Correct C3 advances once");
practice.Receive(Heard(52, 1700), 35);
Check(practice.StringIndex == 2, "Cannot advance again without muting");
long timeMs = 2000;
foreach (var midi in new[] { 52, 55, 60, 64 })
{
    practice.Receive(Mute(timeMs), 35);
    practice.Receive(Heard(midi, timeMs + 100), 35);
    timeMs += 200;
}
Check(practice.Complete && practice.CompletedChecks == 1, "All C strings complete check");
Check(practice.LastCheckSeconds > 0, "Guided check duration recorded");
practice.Receive(Heard(64, 4000), 35);
Check(practice.CompletedChecks == 1, "Completed check does not repeat");
practice.Retry();
Check(!practice.Complete && practice.WaitingForMute, "Retry resets progress and requires mute");

practice.Configure(0, 1, true);
foreach (var shape in Chords.All.Take(2))
{
    foreach (var index in Enumerable.Range(0, 6).Where(i => shape.Frets[i] >= 0))
    {
        practice.Receive(Mute(timeMs), 35);
        practice.Receive(Heard(shape.MidiAt(index), timeMs + 100), 35);
        timeMs += 200;
    }
}
Check(practice.CompletedChecks == 2 && practice.Chord.Name == "Em", "Em/Am alternates and counts checks");
practice.Configure(-100, 900, true);
Check(practice.Chord.Name == "Em", "Saved chord indices are bounded");

// End-to-end: synthetic guitar harmonics -> detector decisions -> guided checks.
for (var chordIndex = 0; chordIndex < Chords.All.Length; chordIndex++)
{
    var audio = new PitchProcessor(48000);
    var exercise = new ChordPractice();
    exercise.Configure(chordIndex, 0, false);
    long now = 10000;
    foreach (var index in Enumerable.Range(0, 6).Where(i => exercise.Chord.Frets[i] >= 0))
    {
        for (var i = 0; i < 3; i++)
            exercise.Receive(audio.Process(silent, now += 43, settings), 35);
        var frequency = 440 * Math.Pow(2, (exercise.Chord.MidiAt(index) - 69) / 12.0);
        var picked = Tone(frequency, harmonics: true);
        for (var i = 0; i < 3; i++)
            exercise.Receive(audio.Process(picked, now += 43, settings), 35);
    }
    Check(exercise.Complete && exercise.Mistakes == 0,
        $"End-to-end guided {Chords.All[chordIndex].Name} check");
}

var logDirectory = Path.Combine(Path.GetTempPath(), $"fftruebard-test-{Guid.NewGuid():N}");
try
{
    using var recorder = new DebugRecorder(logDirectory);
    recorder.Record(accepted, "Test interface", 48000, 1, "A4", "Passed", "blocked:practice", 0);
    Check(!Directory.Exists(logDirectory), "Logging off performs no file writes");
    recorder.Start();
    Check(recorder.Recording, "Debug recording enabled");
    recorder.Record(accepted, "Test interface", 48000, 1, "A4", "Passed", "blocked:practice", 0);
    recorder.Record(accepted with { Decision = "stabilizing" }, "Test interface", 48000, 1, "A4", "Waiting", "blocked:practice", 0);
    recorder.Stop();
    Check(!recorder.Recording && recorder.Status == "Saved", "Stop flushes log");
    var lines = File.ReadAllLines(recorder.LastPath);
    Check(lines.Length == 4, "Session, accepted frame, rejected frame, end written");
    foreach (var line in lines)
    {
        using var doc = JsonDocument.Parse(line);
        Check(doc.RootElement.TryGetProperty("type", out _), "Every JSONL row parses");
    }
    using var row = JsonDocument.Parse(lines[1]);
    var f = row.RootElement.GetProperty("frame");
    Check(Math.Abs(f.GetProperty("frequency").GetDouble() - 440) < 1, "Logged Hz accurate");
    Check(f.GetProperty("settings").GetProperty("rmsGate").GetSingle() == settings.RmsGate, "Settings included");
    Check(row.RootElement.GetProperty("mappedAction").GetString() == "blocked:practice", "Practice block recorded");
    recorder.Start();
    recorder.Stop();
    Check(Directory.GetFiles(logDirectory).Length == 2, "Restart creates distinct file");
}
finally
{
    if (Directory.Exists(logDirectory))
    {
        foreach (var file in Directory.GetFiles(logDirectory))
            File.Delete(file);
        Directory.Delete(logDirectory);
    }
}

Console.WriteLine($"PASS: {checks} regression checks (pitch, gating, practice, diagnostics).");

Check(accepted.OnsetTimestampMs == 1000, "Accepted note retains first agreeing-frame time");
var guitarTuner = new GuitarTuner();
for (var i = 0; i < 5; i++)
    guitarTuner.Receive(Heard(40, 1000 + i * 43, -12), 0);
Check(guitarTuner.Reading?.Target == "E2" && Math.Abs(guitarTuner.Reading.Cents + 12) < 0.1, "Tuner measures flat E2");
guitarTuner.Receive(Heard(52, 1500), 0);
Check(guitarTuner.Reading?.Cents < 0, "Median tuner rejects a one-frame octave outlier");
guitarTuner.Reset();
guitarTuner.Receive(Heard(52, 1550), 0);
Check(Math.Abs(guitarTuner.Reading!.Cents - 1200) < 0.1, "Manual tuner reports octave distance, not a false in-tune result");
guitarTuner.Receive(Heard(45, 1600, 6), -1);
Check(guitarTuner.Reading?.Target == "A2", "Auto tuner chooses nearest open string");
guitarTuner.Receive(Heard(48, 1700), -1);
Check(guitarTuner.Reading is null, "Auto tuner rejects fretted C3 outside open-string range");
guitarTuner.Receive(Mute(1800), 0);
Check(guitarTuner.Reading is null, "Silent input clears tuner");
guitarTuner.Receive(Heard(40, 1900) with { Confidence = 0.2f }, 0);
Check(guitarTuner.Reading is null, "Low confidence cannot show in tune");

var calibration = new CalibrationWizard();
calibration.Start(0);
for (var i = 0; i < 100; i++)
    calibration.Receive(Mute(i * 43) with { Rms = 0.001f, Peak = 0.002f });
calibration.Tick(5000);
Check(calibration.Phase == "ready-soft", "Calibration pauses after noise measurement");
calibration.Begin("soft", 6000);
for (var i = 0; i < 100; i++)
    calibration.Receive(Heard(40, 6000 + i * 43) with { Rms = i % 10 < 2 ? 0.001f : 0.03f, Peak = 0.06f });
calibration.Tick(11000);
Check(calibration.Phase == "ready-normal", "Calibration pauses before normal picks");
calibration.Begin("normal", 12000);
for (var i = 0; i < 100; i++)
    calibration.Receive(Heard(40, 12000 + i * 43) with { Rms = 0.1f, Peak = i == 0 ? 1 : 0.2f });
calibration.Tick(17000);
var suggestion = calibration.Suggestion;
Check(suggestion is not null && suggestion.Gate > 0.001 && suggestion.Gate < 0.03, "Suggested gate separates idle noise and soft picks");
Check(suggestion!.StableFrames == 2, "Consistent notes suggest two stable frames");
Check(suggestion.Warning.Contains("Clipping"), "Calibration warns about clipped picks");
Check(settings.RmsGate == 0.004f, "Calibration does not change detection settings by itself");
calibration.Start(20000);
calibration.Tick(25000);
Check(calibration.Phase == "failed" && calibration.Suggestion is null, "No audio cannot produce calibration suggestions");
calibration.Start(30000);
calibration.Cancel();
Check(!calibration.Running, "Calibration cancels cleanly");
calibration.Start(0);
for (var i = 0; i < 100; i++)
    calibration.Receive(Mute(i * 43) with { Rms = 0.02f, Peak = 0.04f });
calibration.Tick(5000);
calibration.Begin("soft", 6000);
for (var i = 0; i < 100; i++)
    calibration.Receive(Heard(40, 6000 + i * 43) with { Rms = 0.025f, Peak = 0.05f });
calibration.Tick(11000);
calibration.Begin("normal", 12000);
for (var i = 0; i < 100; i++)
    calibration.Receive(Heard(40, 12000 + i * 43) with { Rms = 0.07f, Peak = 0.14f });
calibration.Tick(17000);
Check(calibration.Suggestion is null, "Overlapping noise and soft notes produce no unsafe gate suggestion");

var labelled = new LabelledTest();
labelled.Start(0, 2, 0);
labelled.Receive(Mute(0));
Check(labelled.Phase == "count-in" && labelled.Expected == "E2", "Labelled trial arms after silence");
labelled.Tick(1000);
labelled.Receive(Heard(40, 1100));
labelled.Receive(Heard(52, 1200));
labelled.Receive(Heard(41, 1300));
labelled.Receive(Heard(40, 3100));
labelled.Tick(3100);
var trial = labelled.Trials.Single();
Check(trial.Correct == 1 && trial.OctaveErrors == 1 && trial.WrongNotes == 1, "Labelled test distinguishes correct, octave, and pitch errors");
Check(trial.ExtraDetections == 2 && trial.Accepted.Length == 3, "Late frames excluded and extras counted");
labelled.Receive(Mute(3200));
labelled.Tick(6200);
Check(labelled.Phase == "complete" && labelled.Trials[1].Accepted.Length == 0, "Empty window records a no-detection trial");
Check(labelled.Trials[1].Frames == 0, "Missing audio is distinguishable from gated audio");
labelled.Start(0, 1, 10000);
labelled.Receive(Mute(10000));
labelled.Receive(Heard(40, 10500));
Check(labelled.Phase == "mute", "Early pick resets the test cue");
labelled.Receive(Mute(10600));
labelled.Tick(11600);
Check(labelled.Phase == "listen", "New cue follows a fresh mute");
labelled.Cancel();
Check(!labelled.Running, "Labelled test can be cancelled");
labelled.Start(-1, 5, 0);
Check(labelled.TotalTrials == 30, "All-string test schedules five picks per string");
labelled.Tick(15000);
Check(labelled.Phase == "failed" && !labelled.Running, "No quiet audio times out rather than waiting forever");
labelled.Start(5, 1, 20000);
labelled.Receive(Mute(20000));
labelled.Tick(21000);
labelled.Receive(Heard(64, 21100));
labelled.Tick(23000);
Check(labelled.Trials.Single().Expected == "E4" && labelled.Trials.Single().Correct == 1, "High-E test uses correct octave label");
var trialJson = JsonSerializer.Serialize(labelled.Trials.Single());
using (var trialDoc = JsonDocument.Parse(trialJson))
    Check(trialDoc.RootElement.GetProperty("Accepted")[0].GetProperty("Frequency").GetDouble() > 329,
        "Labelled report retains frequency measurements");

var beatClock = new BeatClock();
beatClock.Start(1000, 60);
Check(beatClock.Observe(4999, 0) is null, "Count-in notes are not scored");
Check(beatClock.Observe(5030, 0) == 30, "Timing offset measured relative to software beat");
Check(beatClock.Observe(5040, 0) is null, "Same beat cannot score twice");
Check(beatClock.Observe(6130, 30) == 100, "Positive timing compensation subtracts measured lateness");
Check(beatClock.ScoredNotes == 2 && beatClock.OnTimeNotes == 1, "Timing counts separate on-time and late notes");
Check(Math.Abs(beatClock.MeanAbsoluteOffsetMs - 65) < 0.1, "Mean absolute timing offset correct");
beatClock.Stop();
Check(beatClock.Observe(7000, 0) is null, "Stopped metronome does not score");
beatClock.Start(0, 120);
Check(beatClock.BeatAt(1250) == 2 && Math.Abs(beatClock.PhaseAt(1250) - 0.5) < 0.01, "Visual beat follows selected tempo");
var clicks = new ClickSignal(60);
var clickSamples = Enumerable.Range(0, 96000).Select(_ => clicks.Next()).ToArray();
Check(clickSamples.All(v => float.IsFinite(v) && Math.Abs(v) <= 0.12f), "Click audio is finite and level-limited");
Check(clickSamples.Take(960).Any(v => v != 0) && clickSamples.Skip(48000).Take(960).Any(v => v != 0), "Sample clock produces regular clicks");
Check(clickSamples.Skip(960).Take(47040).All(v => v == 0), "Silence between metronome clicks");

var historyDirectory = Path.Combine(Path.GetTempPath(), $"fftruebard-history-{Guid.NewGuid():N}");
try
{
    var path = Path.Combine(historyDirectory, "practice-history.json");
    var history = new PracticeHistory(path);
    Check(!File.Exists(path), "Opening history does not create files until practice");
    for (var i = 0; i < 5; i++)
        history.Observe("Em", "Am", "Em", 6, i > 1, i == 4);
    var reloaded = new PracticeHistory(path);
    Check(reloaded.Sessions.Single().Attempts == 5 && reloaded.Sessions.Single().Correct == 3, "History survives reload");
    Check(reloaded.Sessions.Single().StringMisses["Em, string 6"] == 2, "History stores mistake locations");
    Check(reloaded.Sessions.Single().CompletedChecks == 1, "Completed chord checks recorded");
    Check(reloaded.Recommend().First == "Em" && reloaded.Recommend().Second == "Am", "Recommendation prioritises observed weak pair");
    reloaded.NewSession();
    reloaded.Observe("C", "G", "C", 5, true, false);
    Check(reloaded.Sessions.Count == 2, "New exercise gets separate session");
    for (var i = 0; i < 202; i++)
    {
        reloaded.NewSession();
        reloaded.Observe("C", "G", "C", 5, true, false);
    }
    Check(reloaded.Sessions.Count == 200, "History retention bounded to 200 sessions");
    File.WriteAllText(path, "invalid JSON");
    var broken = new PracticeHistory(path);
    broken.Observe("Em", "", "Em", 6, true, true);
    Check(File.ReadAllText(path) == "invalid JSON", "Corrupt history preserved instead of overwritten");
    Check(broken.Status.Contains("not be overwritten"), "History load failure visible");
}
finally
{
    if (Directory.Exists(historyDirectory))
    {
        foreach (var file in Directory.GetFiles(historyDirectory))
            File.Delete(file);
        Directory.Delete(historyDirectory);
    }
}
Console.WriteLine($"PASS: {checks} total checks, including tuner, calibration, labelled tests, metronome, and saved history.");

foreach (var (text, canonical, midi) in new[]
{
    ("E2", "E2", 40), (" e4 ", "E4", 64), ("f#3", "F#3", 54), ("Bb3", "A#3", 58),
    ("Db3", "C#3", 49), ("Cb3", "B2", 47), ("B#2", "C3", 48), ("C#2", "C#2", 37), ("E6", "E6", 88),
})
    Check(MappingRules.TryNote(text, out var normalized, out var noteMidi) && normalized == canonical && noteMidi == midi,
        $"Mapping note normalization: {text}");
foreach (var invalid in new string?[] { null, "", "E", "E-2", "C2", "F6", "B1", "E10", "H3", "E2;A2", "E##3" })
    Check(!MappingRules.TryNote(invalid, out _, out _), $"Invalid mapping note rejected: {invalid}");
Check(MappingRules.Notes.Count == 52 && MappingRules.Notes.Distinct().Count() == 52, "Supported note list is bounded and unique");
foreach (var note in MappingRules.Notes)
    Check(MappingRules.TryNote(note, out var same, out _) && same == note, "Every selectable note validates");

foreach (var key in MappingRules.Keys)
{
    Check(MappingRules.TryBinding($"Key:{key}", out var binding) && binding.Kind == BindingKind.Keyboard && binding.Key == key,
        $"Selectable keyboard key validates: {key}");
    Check(MappingRules.TryBinding(binding.Canonical, out var roundTrip) && roundTrip == binding,
        $"Keyboard binding round-trips: {key}");
}
for (var button = 1; button <= 5; button++)
    Check(MappingRules.TryBinding($"mouse{button}", out var mouse) && mouse.Canonical == $"Mouse{button}", "Mouse buttons 1-5 validate");
Check(MappingRules.TryBinding(" key:alt+control+shift+esc ", out var modifiers) &&
    modifiers.Canonical == "Key:Ctrl+Shift+Alt+Escape", "Modifier ordering, aliases, and case normalized");
Check(MappingRules.TryBinding("Key:Ctrl+1", out _), "Ctrl-number binding supported");
Check(MappingRules.TryBinding("", out var ignore) && ignore.Kind == BindingKind.Ignore, "Empty legacy action means ignore");
foreach (var invalid in new string?[] { null, "Mouse0", "Mouse6", "Mouse10", "Mouse01", "Key:", "Key:F25", "Key:Meta+1",
    "Key:Ctrl+Ctrl+1", "Key:Control+Ctrl+1", "Key:Shift+LeftShift", "Key:Ctrl+RightCtrl", "Key:Alt+Alt",
    "Key:A+W", "Key:Ctrl++1", "Key:Ctrl+Mouse1", "Key:1,2", "Macro:123" })
    Check(!MappingRules.TryBinding(invalid, out _), $"Invalid/unsupported mapping action rejected: {invalid}");

var mappings = new Dictionary<string, string> { ["E2"] = "Mouse1", ["A2"] = "Mouse2", ["E4"] = "", ["legacy-note"] = "legacy-action" };
var beforeInvalid = JsonSerializer.Serialize(mappings);
Check(!MappingRules.TrySet(mappings, "bad note", "Key:1", out _, out _) && JsonSerializer.Serialize(mappings) == beforeInvalid,
    "Invalid note never changes saved mappings");
Check(!MappingRules.TrySet(mappings, "E2", "Key:Unknown", out _, out _) && JsonSerializer.Serialize(mappings) == beforeInvalid,
    "Invalid action never overwrites an existing binding");
Check(MappingRules.TrySet(mappings, "e2", "Key:shift+1", out var savedNote, out _) && savedNote == "E2" && mappings["E2"] == "Key:Shift+1",
    "Updating a note canonicalizes its binding");
Check(mappings["A2"] == "Mouse2" && mappings["legacy-note"] == "legacy-action", "Other existing and unsupported rows preserved");
Check(MappingRules.TrySet(mappings, "Bb3", "Mouse4", out _, out _) && mappings["A#3"] == "Mouse4", "New flat-labelled note saved canonically");
Check(MappingRules.TrySet(mappings, "D3", "Key:Shift+1", out _, out _), "Intentional many-notes-to-one-action binding allowed");
var savedMappings = JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(mappings))!;
Check(savedMappings["E2"] == "Key:Shift+1" && savedMappings["E4"] == "", "Existing dictionary storage format round-trips with modifier bindings");
Check(MappingRules.Resolve(savedMappings, "E2", true, false).LogValue == "log-only:Key:Shift+1", "Saved keyboard mapping resolves to log-only action");
Check(MappingRules.Resolve(savedMappings, "A#3", true, false).Action == "Mouse4", "Saved side-button mapping resolves");
Check(MappingRules.Resolve(savedMappings, "E2", true, true).Status == "blocked:practice-tool", "Practice/tool state blocks mapping route");
Check(MappingRules.Resolve(savedMappings, "E2", false, false).Status == "disabled", "Mapping enable switch respected");
Check(MappingRules.Resolve(savedMappings, "E4", true, false).Status == "ignored", "Explicit ignore distinguished from missing mapping");
Check(MappingRules.Resolve(savedMappings, "F2", true, false).Status == "unmapped", "Unassigned note has no action");
savedMappings["E2"] = "invalid action";
Check(MappingRules.Resolve(savedMappings, "E2", true, false).Status == "invalid-mapping", "Unsupported stored action cannot reach dispatcher");
Check(MappingRules.Resolve(savedMappings, "legacy-note", true, false).Status == "unsupported-note", "Unsupported note cannot reach dispatcher");
savedMappings.Remove("A2");
Check(savedMappings.ContainsKey("E4") && !savedMappings.ContainsKey("A2"), "Removing one row leaves other bindings intact");

var captureNote = new MappingNoteCapture();
captureNote.Arm(1000);
Check(captureNote.Receive(Mute(900)) is null && captureNote.Armed, "Note selection ignores queued frames from before arming");
Check(captureNote.Receive(Heard(40, 1020) with { Note = "E2" }) is null, "Note selection requires fresh muting first");
captureNote.Receive(Mute(1100));
Check(captureNote.Receive(Heard(49, 1140) with { Note = "C#3", Decision = "stabilizing" }) is null,
    "Note selection waits for confirmed detection");
Check(captureNote.Receive(Heard(49, 1180) with { Note = "C#3" }) == "C#3" && !captureNote.Armed, "Confirmed pitch selects a draft note once");
Check(captureNote.Receive(Heard(40, 1220) with { Note = "E2" }) is null, "Later notes do not replace completed selection");
captureNote.Arm(2000);
captureNote.Tick(17000);
Check(!captureNote.Armed && captureNote.Status.Contains("timed out"), "Note selection times out without audio");
captureNote.Arm(20000);
captureNote.Cancel();
Check(!captureNote.Armed && captureNote.Receive(Mute(20010)) is null, "Cancelled selection stops listening");
captureNote.Reset();
Check(!captureNote.Armed && captureNote.Status == "", "Manual draft selection clears stale capture status");
Check(JsonSerializer.Serialize(mappings) != beforeInvalid && mappings["A2"] == "Mouse2", "Note selection does not mutate mappings");
Console.WriteLine($"PASS: {checks} total checks, including mapping validation, persistence, preview routing, and note selection.");

var health = new AudioHealth();
Check(health.Snapshot(1000, 0, 0).State == "stopped", "Health initially stopped");
health.Begin(1000);
Check(health.Snapshot(1500, 0, 0).State == "starting", "Health allows startup grace");
Check(health.Snapshot(4000, 0, 0).State == "no-audio-callbacks", "Missing callbacks are not labelled silence");
health.ObserveBlock(4000, 2048);
Check(health.Snapshot(4000, 0, 0).State == "processing-stalled", "Raw callbacks distinguished from missing DSP frames");
health.ObserveFrame(Mute(4000));
Check(health.Snapshot(4000, 0, 0).State == "silent-input", "Real zero audio with arriving frames recognised");
health.ObserveFrame(Mute(4000) with { Rms = 0.001f, Peak = 0.002f });
Check(health.Snapshot(4000, 0, 0).State == "below-noise-gate", "Quiet noise below gate is distinct from zero signal");
health.ObserveFrame(Heard(40, 4000));
Check(health.Snapshot(4000, 0, 0).State == "signal-present", "Health reports active signal");
health.ObserveFrame(Heard(40, 4000) with { Peak = 0.99f });
Check(health.Snapshot(4000, 0, 0).ClippedFrames == 1 && health.Snapshot(4000, 0, 0).State == "clipping", "Clipping is visible and counted");
Check(health.Snapshot(6000, 1, 2).State == "capture-stalled", "Capture stall detected after fresh frames stop");
health.ObserveBlock(6100, 2048);
Check(health.Snapshot(6100, 1, 2).State == "processing-stalled", "Processing stall detected while capture continues");
health.ObserveFrame(Heard(40, 6100) with { Rms = float.NaN });
var invalidHealth = health.Snapshot(6100, 1, 2);
Check(invalidHealth.State == "invalid-samples" && invalidHealth.Rms == 0, "Health sanitises invalid samples for JSON export");
health.Stop();
Check(!health.Running && health.Snapshot(6200, 1, 2).State == "stopped", "Stopped capture never looks like a silent guitar");
health.Begin(7000);
Check(health.Snapshot(7000, 1, 2).ProcessedFrames == 0 && health.Snapshot(7000, 1, 2).DroppedAudioBlocks == 1,
    "Capture restart clears per-capture metrics and preserves explicit lifetime drops");
health.ObserveBlock(7100, 0);
Check(health.Snapshot(7100, 0, 0).Blocks == 0, "Empty callbacks do not count as arriving audio");

var manual = new ManualChecklist();
manual.Start(1000);
Check(manual.Steps.Count == 5 && !manual.Next(1100), "Checklist cannot auto-pass or advance an untested step");
manual.Observe(Mute(900));
manual.Observe(Mute(1100));
manual.Observe(Heard(40, 1200) with { Note = "E2" });
Check(manual.Current.Frames == 2 && manual.Current.AcceptedNotes["E2"] == 1, "Checklist tracks only frames from this session");
Check(!manual.SetResult("automatic pass", ""), "Checklist accepts only explicit supported human results");
manual.SetResult("Issue", new string('x', 2200));
Check(manual.Current.Notes.Length == 2000, "Checklist notes bounded");
Check(manual.Next(1300) && manual.Index == 1 && manual.Current.Frames == 0, "Step metrics kept separate");
while (manual.Running)
{
    manual.SetResult("Skipped", "Not available on test machine");
    manual.Next(2000);
}
Check(manual.Outcome == "Completed" && manual.FinishedMs == 2000, "Completed checklist retains labelled results");
manual.Cancel(3000);
Check(manual.Outcome == "Completed", "Cancelling inactive checklist does not rewrite completed outcome");
manual.Start(4000);
manual.UpdateNotes("Cable disconnected before grading");
manual.Cancel(4500);
Check(manual.Outcome == "Cancelled" && manual.Current.Notes.Contains("Cable"), "Partial notes survive cancellation");

var supportDirectory = Path.Combine(Path.GetTempPath(), $"fftruebard-support-{Guid.NewGuid():N}");
try
{
    var backupDirectory = Path.Combine(supportDirectory, "settings-backups");
    var store = new SettingsBackups(backupDirectory);
    var editable = new EditableSettings("NUX test interface", 0, 0.15f, 0.004f, 2, 150, 100, true, MappingRules.DefaultMappings());
    var originalBackup = store.Create("Before mapping edit", editable);
    editable.Mapping["E2"] = "Key:Ctrl+1";
    Check(originalBackup.Settings.Mapping["E2"] == "Mouse1", "Backup is a deep copy, not a mutable live dictionary");
    var persistedBackup = new SettingsBackups(backupDirectory).List().Single();
    Check(persistedBackup.Settings.Mapping["E2"] == "Mouse1" && persistedBackup.Settings.Gate == 0.004f,
        "Recovery points persist mappings and detection settings");
    var restoredSettings = persistedBackup.Settings.Copy();
    restoredSettings.Mapping["E2"] = "Key:2";
    Check(persistedBackup.Settings.Mapping["E2"] == "Mouse1", "Restoring/editing a copy preserves the original recovery point");
    for (var i = 0; i < 22; i++)
        store.Create($"Edit {i}", editable);
    var retained = store.List();
    Check(retained.Count == SettingsBackups.Retention && retained[0].Reason == "Edit 21", "Backup retention keeps newest 20 in order");
    File.WriteAllText(Path.Combine(backupDirectory, "settings-unreadable.json"), "user recovery notes");
    store.List();
    Check(File.ReadAllText(Path.Combine(backupDirectory, "settings-unreadable.json")) == "user recovery notes" && store.Warning.Length > 0,
        "Unreadable recovery files preserved and reported");
    var invalidRefused = false;
    try { store.Create("Invalid", editable with { Gate = float.NaN }); }
    catch (InvalidDataException) { invalidRefused = true; }
    Check(invalidRefused, "Invalid recovery snapshots rejected before writing");
    var tooLargeSettings = editable.Copy();
    tooLargeSettings.Mapping["oversized"] = new string('x', SettingsBackups.MaxFileBytes + 1);
    var largeBackupRefused = false;
    try { store.Create("Too large", tooLargeSettings); }
    catch (InvalidDataException) { largeBackupRefused = true; }
    Check(largeBackupRefused, "Oversized recovery files are not created as unusable undo points");

    var logDirectory2 = Path.Combine(supportDirectory, "diagnostics");
    var reportsDirectory = Path.Combine(supportDirectory, "training-reports");
    Directory.CreateDirectory(logDirectory2);
    Directory.CreateDirectory(reportsDirectory);
    File.WriteAllText(Path.Combine(logDirectory2, "sample.jsonl"), "{\"device\":\"NUX test\",\"note\":\"E2\"}\n");
    File.WriteAllText(Path.Combine(reportsDirectory, "trial.json"), "{\"result\":\"Issue\"}");
    File.WriteAllText(Path.Combine(logDirectory2, "private.txt"), "not exportable");
    var export = new DiagnosticExport(supportDirectory);
    var sources = export.ListSources();
    Check(sources.Count == 2 && sources.All(s => !s.RelativePath.Contains("settings-backups")), "Export lists only allowlisted logs/reports, not backups or other files");
    var frozen = export.Preview(sources.Select(s => s.RelativePath), "{\"version\":1}", "{\"device\":\"[omitted]\"}");
    Check(frozen.Items.Count == 4 && frozen.Items.All(i => i.Sha256.Length == 64), "Preview freezes generated metadata and selected files with hashes");
    File.WriteAllText(Path.Combine(logDirectory2, "sample.jsonl"), "changed after preview");
    var refusedUnreviewed = false;
    try { export.CreateArchive(frozen, reviewed: false); }
    catch (InvalidOperationException) { refusedUnreviewed = true; }
    Check(refusedUnreviewed, "Export requires explicit review approval");
    var archiveFile = export.CreateArchive(frozen, reviewed: true);
    using (var zip = System.IO.Compression.ZipFile.OpenRead(archiveFile))
    {
        Check(zip.Entries.Count == 4 && zip.GetEntry("session-info.json") is not null, "Archive contains only the preview manifest entries");
        using var reader = new StreamReader(zip.GetEntry("diagnostics/sample.jsonl")!.Open());
        Check(reader.ReadToEnd().Contains("NUX test"), "Export bytes match the reviewed snapshot, not files modified afterwards");
    }
    foreach (var forbidden in new[] { "../private.json", "diagnostics/private.txt", "settings-backups/settings-unreadable.json",
        "training-reports/../../private.json", "C:/private.json", "diagnostics\\sample.jsonl", "training-reports/..\\private.json" })
    {
        var rejected = false;
        try { export.Preview([forbidden], "{}", "{}"); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, $"Export rejects non-allowlisted/traversal path: {forbidden}");
    }
    using (var large = new FileStream(Path.Combine(logDirectory2, "large.jsonl"), FileMode.CreateNew))
        large.SetLength(DiagnosticExport.MaxBytes + 1);
    var oversizedRejected = false;
    try { export.Preview(["diagnostics/large.jsonl"], "{}", "{}"); }
    catch (InvalidDataException) { oversizedRejected = true; }
    Check(oversizedRejected, "Oversized export refused before allocating contents");
    var unsafeArchiveRefused = false;
    try { export.CreateArchive(new(DateTime.UtcNow, [new("../unsafe.json", [])]), reviewed: true); }
    catch (InvalidDataException) { unsafeArchiveRefused = true; }
    Check(unsafeArchiveRefused, "Archive entry names cannot escape the archive root");
    var settingsOnly = export.Preview([], "{}", "{}");
    Check(settingsOnly.Items.Count == 2, "No log/report attachments are automatically selected");
}
finally
{
    if (Directory.Exists(supportDirectory))
    {
        foreach (var file in Directory.GetFiles(supportDirectory, "*", SearchOption.AllDirectories))
            File.Delete(file);
        foreach (var directory in Directory.GetDirectories(supportDirectory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            Directory.Delete(directory);
        Directory.Delete(supportDirectory);
    }
}
Console.WriteLine($"PASS: {checks} total checks, including audio health, checklist, recovery, and reviewed diagnostic export.");
LessonTests.Run(Check);
Console.WriteLine($"PASS: {checks} total checks, including lesson discovery, progress, seeking, and A-B audio looping.");
LessonPitchTests.Run(Check);
Console.WriteLine($"PASS: {checks} total checks, including local pitch sequences and visual pitch-order grading.");
LessonCatalogTests.Run(Check);
Console.WriteLine($"PASS: {checks} total checks, including guided exercise catalogs, references, and audio cues.");
ReferenceEvaluationTests.Run(Check);
Console.WriteLine($"PASS: {checks} total checks, including independent reference-audio scoring and native-channel replay.");
