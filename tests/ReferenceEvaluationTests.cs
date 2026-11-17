using System.Text.Json;
using FFTrueBard.Detection;
using FFTrueBard.Lessons;
using NAudio.Wave;

internal static class ReferenceEvaluationTests
{
    public static void Run(Action<bool, string> check)
    {
        var sequence = new LessonSequence(1, 1, "Independent reference targets", "Synthesized test", [new(69), new(69), new(71)]);
        var definition = new ReferenceAudioCase
        {
            Title = "Scoring test", Exercise = 1, TimingsReviewed = true, TimingSource = "Independent unit-test labels",
            AnalysisEndSeconds = 3.5, TimingUncertaintyMs = 40,
            Timings = [new(0.5, 1.5), new(1.5, 2.5), new(2.5, 3.5)],
        };
        DetectionFrame Note(double end, int midi = 69, string decision = "sustained", double cents = 0) =>
            new((long)Math.Round(end * 1000), 440 * Math.Pow(2, (midi - 69) / 12.0), SequenceNote.NoteName(midi),
                midi, cents, 0.1f, 0.2f, 0.95f, decision, false, definition.Settings);
        ReferenceFrame F(double start, int midi = 69, string decision = "sustained", double cents = 0) =>
            new(start, start + 0.04, Note(start + 0.04, midi, decision, cents));
        ReferenceFrame[] frames =
        [
            F(0.1, decision: "accepted"), // Count-in or other unlabelled audio: not scored as a guitar mistake.
            F(0.6, decision: "accepted"), F(0.7, decision: "accepted"), F(0.8, 57, "accepted"),
            new(0.9, 0.94, Note(0.94) with { Midi = -1, Frequency = 0, Decision = "unvoiced" }),
            F(1.0, 71), F(1.1, cents: 40),
            new(1.2, 1.24, Note(1.24) with { Frequency = double.NaN }),
            new(1.3, 1.34, Note(1.34) with { Peak = 0.99f }),
            F(1.48), // Crosses a labelled note boundary: excluded from steady-pitch accuracy.
            F(1.6), // Repeated pitch tracks correctly, but there is no new accepted event.
            new(2.6, 2.64, Note(2.64, 71, "accepted") with { Confidence = 0.7f }),
            F(2.7, 71, "accepted"),
        ];
        var result = ReferenceAudioEvaluation.Score(definition, sequence, frames);
        check(result.SteadyFrames.Total == 11 && result.FramesExcludedFromSteadyScore == 2, "Only full native frames inside guarded note interiors score");
        check(result.SteadyFrames.Voiced == 9 && result.SteadyFrames.Unvoiced == 1 && result.SteadyFrames.Invalid == 1, "Unvoiced and invalid frames stay visible in coverage denominator");
        check(result.SteadyFrames.CorrectMidi == 7 && result.SteadyFrames.WithinTolerance == 6, "MIDI accuracy and cents tolerance are distinct measurements");
        check(result.SteadyFrames.OctaveErrors == 1 && result.SteadyFrames.OtherWrongPitches == 1, "Octave errors are separated from other wrong pitches");
        check(result.SteadyFrames.Clipped == 1, "Clipping is reported, not silently removed from reference coverage");
        check(result.RecoveredNotes == 2 && result.AcceptedEventsInLabels == 5 && result.AdditionalCorrectAcceptedEvents == 1, "Duplicate accepted events cannot inflate per-note recovery");
        check(result.AcceptedEventsOutsideLabels == 1 && result.OctaveErrorAcceptedEvents == 1, "Count-in acceptances and labelled octave events reported separately");
        check(result.RepeatedTargets == 1 && result.RepeatedTargetsRecovered == 0, "Tracking a repeated pitch does not imply detecting its repeated pick");
        check(result.Notes[1].Pitch.WithinTolerance == 1 && !result.Notes[1].RecoveredByAcceptedEvent, "Per-note report distinguishes sounding pitch from accepted events");
        check(result.NotesWithSteadyFrame == 3 && result.NotesWithCorrectSteadyPitch == 3, "Per-note pitch coverage includes all reviewed targets");
        check(Math.Abs(result.SteadyFrames.CorrectPitchCoveragePercent!.Value - 600.0 / 11) < 0.001, "Coverage does not hide unvoiced intervals behind conditional pitch accuracy");
        check(Math.Abs(result.AcceptedEventNoteCoveragePercent!.Value - 200.0 / 3) < 0.001, "Accepted-event coverage uses every reference note, not only pitch changes");
        var empty = ReferenceAudioEvaluation.Score(definition, sequence, []);
        check(empty.SteadyFrames.CorrectPitchCoveragePercent is null && empty.SteadyFrames.CorrectMidiAmongVoicedPercent is null, "No-frame reports cannot claim 100 percent accuracy");
        var low = ReferenceAudioEvaluation.Score(definition, sequence,
            [new(0.6, 0.64, Note(0.64, decision: "accepted") with { Peak = float.NaN })]);
        check(low.RecoveredNotes == 0, "Invalid accepted events are not usable practice events");
        foreach (var bad in new[]
        {
            definition with { TimingsReviewed = false }, definition with { TimingSource = "" },
            definition with { Timings = [new(0.5, 1.5)] }, definition with { Channels = [1, 1] },
            definition with { Channels = [0] }, definition with { PitchToleranceCents = double.NaN },
            definition with { AttackGuardMs = 20 }, definition with { ReleaseGuardMs = 20 },
            definition with { AnalysisEndSeconds = 2 },
            definition with { Timings = [new(0, 1), new(0.5, 2), new(2, 3)] },
            definition with { Timings = [new(0, 1), new(1, double.NaN), new(2, 3)] },
        })
        {
            var rejected = false;
            try { bad.Validate(sequence); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unreviewed, overlapping, uncertain, or malformed reference labels rejected");
        }

        var directory = Path.Combine(Path.GetTempPath(), "fftruebard-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var audio = Path.Combine(directory, "independent-stereo.wav");
            var sampleRate = 44100;
            using (var writer = new WaveFileWriter(audio, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
            {
                var samples = new float[sampleRate * 2];
                for (var i = 0; i < sampleRate; i++)
                {
                    if (i < 0.3 * sampleRate || i >= 0.8 * sampleRate) continue;
                    samples[2 * i] = (float)(0.15 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                    samples[2 * i + 1] = (float)(0.15 * Math.Sin(2 * Math.PI * 493.8833 * i / sampleRate));
                }
                writer.WriteSamples(samples, 0, samples.Length);
            }
            var noteFile = Path.Combine(directory, "independent.notes.json");
            File.WriteAllText(noteFile, JsonSerializer.Serialize(sequence with { Notes = [new(69)] }));
            var casePath = Path.Combine(directory, "independent.reference.json");
            File.WriteAllText(casePath, JsonSerializer.Serialize(definition with
            {
                AudioFile = audio, SequenceFile = noteFile, Channels = [1, 2],
                AnalysisEndSeconds = 1, Timings = [new(0.3, 0.8)],
            }));
            var output = Path.Combine(directory, "report");
            var console = Console.Out;
            using var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                ReferenceAudioEvaluation.RunCase(casePath, output);
            }
            finally { Console.SetOut(console); }
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "report.json")));
            var root = report.RootElement;
            check(root.GetProperty("sampleRate").GetInt32() == 44100 && root.GetProperty("frameSamples").GetInt32() == 1881, "Reference runner uses native sample rate and capture-sized frames");
            var channels = root.GetProperty("results");
            check(channels[0].GetProperty("score").GetProperty("steadyFrames").GetProperty("correctPitchCoveragePercent").GetDouble() == 100, "Known left-channel reference is detected correctly");
            check(channels[1].GetProperty("score").GetProperty("steadyFrames").GetProperty("correctMidi").GetInt32() == 0, "Wrong right-channel pitch is not hidden by stereo averaging");
            check(root.GetProperty("source").GetProperty("audioHash").GetString()!.Length == 64, "Reference evidence includes SHA-256 identities");
            check(Directory.GetFiles(output).Length == 3 && Directory.GetFiles(output).All(p => Path.GetExtension(p) is ".json" or ".csv"), "Only metric reports and pitch-frame CSVs written; no audio copies");
            var refused = false;
            try { ReferenceAudioEvaluation.RunCase(casePath, output); }
            catch (IOException) { refused = true; }
            check(refused, "Previous reference evidence is not overwritten");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var folder in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(folder);
            Directory.Delete(directory);
        }
    }
}
