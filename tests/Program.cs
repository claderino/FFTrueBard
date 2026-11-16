using System.Text.Json;
using FFTrueBard.Detection;
using FFTrueBard.Diagnostics;
using FFTrueBard.Practice;

// Dependency-free regression runner: no game installation or audio hardware required.
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
