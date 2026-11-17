using System.Security.Cryptography;
using System.Text.Json;
using FFTrueBard.Detection;
using FFTrueBard.Lessons;
using NAudio.Wave;

internal sealed record ReferenceTiming(double StartSeconds, double EndSeconds);

internal sealed record ReferenceAudioCase
{
    public int SchemaVersion { get; init; } = 1;
    public string Title { get; init; } = "";
    public string AudioFile { get; init; } = "";
    public string SequenceFile { get; init; } = "";
    public int Exercise { get; init; }
    public int[] Channels { get; init; } = [1];
    public double AnalysisEndSeconds { get; init; }
    public bool TimingsReviewed { get; init; }
    public string TimingSource { get; init; } = "";
    public double TimingUncertaintyMs { get; init; }
    public double AttackGuardMs { get; init; } = 55;
    public double ReleaseGuardMs { get; init; } = 45;
    public double PitchToleranceCents { get; init; } = 35;
    public DetectionSettings Settings { get; init; } = new(0.15f, 0.004f, 2, 150, 100);
    public ReferenceTiming[] Timings { get; init; } = [];

    public void Validate(LessonSequence sequence)
    {
        sequence.Validate(Exercise);
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Title) || Title.Length > 200 ||
            !TimingsReviewed || string.IsNullOrWhiteSpace(TimingSource) || TimingSource.Length > 1000 ||
            Channels is null || Channels.Length is < 1 or > 8 || Channels.Any(c => c is < 1 or > 8) ||
            Channels.Distinct().Count() != Channels.Length || !double.IsFinite(AnalysisEndSeconds) ||
            AnalysisEndSeconds is <= 0 or > 600 || !double.IsFinite(TimingUncertaintyMs) ||
            TimingUncertaintyMs is < 0 or > 200 || !double.IsFinite(AttackGuardMs) ||
            AttackGuardMs < TimingUncertaintyMs || AttackGuardMs > 250 ||
            !double.IsFinite(ReleaseGuardMs) || ReleaseGuardMs < TimingUncertaintyMs || ReleaseGuardMs > 250 ||
            !double.IsFinite(PitchToleranceCents) || PitchToleranceCents is < 5 or > 50 ||
            Settings is null || !float.IsFinite(Settings.Threshold) || Settings.Threshold is < 0.02f or > 0.5f ||
            !float.IsFinite(Settings.RmsGate) || Settings.RmsGate is < 0.0001f or > 0.2f ||
            Settings.StableFrames is < 1 or > 10 || Settings.RetriggerMs is < 0 or > 1000 ||
            Settings.ReleaseMs is < 40 or > 1000 || Timings is null || Timings.Length != sequence.Notes.Length)
            throw new InvalidDataException("Reference case needs a reviewed, bounded timeline matching the note sequence and valid detector settings.");
        double previousEnd = 0;
        foreach (var timing in Timings)
        {
            if (timing is null || !double.IsFinite(timing.StartSeconds) || !double.IsFinite(timing.EndSeconds) ||
                timing.StartSeconds < previousEnd || timing.EndSeconds <= timing.StartSeconds ||
                timing.EndSeconds > AnalysisEndSeconds)
                throw new InvalidDataException("Reference note intervals must be ordered, non-overlapping, and inside the analysis range.");
            previousEnd = timing.EndSeconds;
        }
    }
}

internal sealed record ReferenceFrame(double StartSeconds, double EndSeconds, DetectionFrame Detection);

internal sealed class ReferencePitchCounts
{
    public int Total { get; private set; }
    public int Voiced { get; private set; }
    public int CorrectMidi { get; private set; }
    public int WithinTolerance { get; private set; }
    public int OctaveErrors { get; private set; }
    public int OtherWrongPitches { get; private set; }
    public int Unvoiced { get; private set; }
    public int Invalid { get; private set; }
    public int Clipped { get; private set; }
    public double? VoicedCoveragePercent => Percent(Voiced, Total);
    public double? CorrectMidiAmongVoicedPercent => Percent(CorrectMidi, Voiced);
    public double? WithinToleranceAmongVoicedPercent => Percent(WithinTolerance, Voiced);
    public double? CorrectPitchCoveragePercent => Percent(WithinTolerance, Total);
    internal static double? Percent(int part, int total) => total == 0 ? null : 100.0 * part / total;

    public void Observe(DetectionFrame frame, int expected, double tolerance)
    {
        Total++;
        if (frame.Peak >= 0.98f) Clipped++;
        if (!double.IsFinite(frame.Frequency) || !double.IsFinite(frame.Cents) ||
            !float.IsFinite(frame.Rms) || !float.IsFinite(frame.Peak) || !float.IsFinite(frame.Confidence) ||
            frame.Midi > 127)
        {
            Invalid++;
            return;
        }
        if (frame.Frequency <= 0 || frame.Midi < 0) { Unvoiced++; return; }
        Voiced++;
        if (frame.Midi == expected)
        {
            CorrectMidi++;
            if (Math.Abs(frame.Cents) <= tolerance) WithinTolerance++;
        }
        else if ((frame.Midi - expected) % 12 == 0) OctaveErrors++;
        else OtherWrongPitches++;
    }
}

internal sealed class ReferenceNoteScore(int number, int midi, ReferenceTiming timing)
{
    public int Number { get; } = number;
    public int ExpectedMidi { get; } = midi;
    public string ExpectedNote => SequenceNote.NoteName(ExpectedMidi);
    public ReferenceTiming Timing { get; } = timing;
    public ReferencePitchCounts Pitch { get; } = new();
    public int AcceptedEvents { get; set; }
    public int CorrectAcceptedEvents { get; set; }
    public int OctaveErrorAcceptedEvents { get; set; }
    public bool RecoveredByAcceptedEvent => CorrectAcceptedEvents > 0;
}

internal sealed record ReferenceScore(ReferencePitchCounts SteadyFrames, ReferenceNoteScore[] Notes,
    int AnalysisFrames, int FramesExcludedFromSteadyScore, int AcceptedEventsOutsideLabels)
{
    public int NotesWithSteadyFrame => Notes.Count(n => n.Pitch.Total > 0);
    public int NotesWithCorrectSteadyPitch => Notes.Count(n => n.Pitch.WithinTolerance > 0);
    public int AcceptedEventsInLabels => Notes.Sum(n => n.AcceptedEvents);
    public int RecoveredNotes => Notes.Count(n => n.RecoveredByAcceptedEvent);
    public int OctaveErrorAcceptedEvents => Notes.Sum(n => n.OctaveErrorAcceptedEvents);
    public int AdditionalCorrectAcceptedEvents => Notes.Sum(n => Math.Max(0, n.CorrectAcceptedEvents - 1));
    public int RepeatedTargets => Notes.Skip(1).Count(n => n.ExpectedMidi == Notes[n.Number - 2].ExpectedMidi);
    public int RepeatedTargetsRecovered => Notes.Skip(1).Count(n =>
        n.ExpectedMidi == Notes[n.Number - 2].ExpectedMidi && n.RecoveredByAcceptedEvent);
    public double? AcceptedEventNoteCoveragePercent => ReferencePitchCounts.Percent(RecoveredNotes, Notes.Length);
}

internal static class ReferenceAudioEvaluation
{
    public static ReferenceScore Score(ReferenceAudioCase definition, LessonSequence sequence, IReadOnlyList<ReferenceFrame> frames)
    {
        definition.Validate(sequence);
        var notes = definition.Timings.Select((t, i) => new ReferenceNoteScore(i + 1, sequence.Notes[i].Midi, t)).ToArray();
        var totals = new ReferencePitchCounts();
        var outsideEvents = 0;
        foreach (var frame in frames)
        {
            var stable = notes.FirstOrDefault(n => frame.StartSeconds >= n.Timing.StartSeconds + definition.AttackGuardMs / 1000 &&
                frame.EndSeconds <= n.Timing.EndSeconds - definition.ReleaseGuardMs / 1000);
            if (stable is not null)
            {
                stable.Pitch.Observe(frame.Detection, stable.ExpectedMidi, definition.PitchToleranceCents);
                totals.Observe(frame.Detection, stable.ExpectedMidi, definition.PitchToleranceCents);
            }
            if (frame.Detection.Decision != "accepted") continue;
            // Emission time, not a claim that this is the physical pick onset. Each event belongs to at most one note.
            var note = notes.FirstOrDefault(n => frame.EndSeconds >= n.Timing.StartSeconds && frame.EndSeconds < n.Timing.EndSeconds);
            if (note is null) { outsideEvents++; continue; }
            note.AcceptedEvents++;
            var detection = frame.Detection;
            if (detection.Midi != note.ExpectedMidi && detection.Midi >= 0 && (detection.Midi - note.ExpectedMidi) % 12 == 0)
                note.OctaveErrorAcceptedEvents++;
            if (detection.Midi == note.ExpectedMidi && double.IsFinite(detection.Cents) &&
                Math.Abs(detection.Cents) <= definition.PitchToleranceCents && detection.Confidence >= 0.8f &&
                float.IsFinite(detection.Confidence) && float.IsFinite(detection.Peak) && detection.Peak < 0.98f &&
                float.IsFinite(detection.Rms) && detection.Rms >= definition.Settings.RmsGate &&
                !detection.Released && detection.Frequency > 0 && double.IsFinite(detection.Frequency))
                note.CorrectAcceptedEvents++;
        }
        return new(totals, notes, frames.Count, frames.Count - totals.Total, outsideEvents);
    }

    public static void RunCase(string casePath, string outputDirectory)
    {
        casePath = LessonLibrary.Normalize(casePath);
        outputDirectory = LessonLibrary.Normalize(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("Choose a new or empty reference output directory.");
        if (!casePath.EndsWith(".reference.json", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(casePath).Length > 256 * 1024)
            throw new InvalidDataException("Use a local .reference.json case, at most 256 KiB.");
        var caseHash = Hash(casePath);
        var definition = JsonSerializer.Deserialize<ReferenceAudioCase>(File.ReadAllText(casePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty reference case.");
        var audioPath = LessonLibrary.Normalize(definition.AudioFile);
        var sequencePath = LessonLibrary.Normalize(definition.SequenceFile);
        if (!new[] { ".mp3", ".wav" }.Contains(Path.GetExtension(audioPath).ToLowerInvariant()))
            throw new InvalidDataException("Reference input must be a local MP3 or WAV.");
        var sequenceHash = Hash(sequencePath);
        var sequence = LessonSequence.Load(sequencePath, definition.Exercise);
        definition.Validate(sequence);
        // Hash the source identities on both sides of analysis to detect edits while the baseline is being measured.
        var audioHash = Hash(audioPath);
        using var reader = new AudioFileReader(audioPath);
        var rate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        if (definition.Channels.Any(c => c > channels) || definition.AnalysisEndSeconds > reader.TotalTime.TotalSeconds)
            throw new InvalidDataException("Requested channels or analysis duration are outside the recording.");
        // Matches GuitarDetector.DetectionLoop. Native channels are selected individually, not averaged or normalised.
        var size = Math.Max(1024, (int)(2048L * rate / 48000));
        var processors = definition.Channels.Select(_ => new PitchProcessor(rate)).ToArray();
        var frames = definition.Channels.Select(_ => new List<ReferenceFrame>()).ToArray();
        var interleaved = new float[size * channels];
        var mono = new float[size];
        long consumed = 0;
        var limit = (long)Math.Floor(definition.AnalysisEndSeconds * rate);
        while (consumed + size <= limit)
        {
            var read = 0;
            while (read < interleaved.Length)
            {
                var got = reader.Read(interleaved, read, interleaved.Length - read);
                if (got == 0) break;
                read += got;
            }
            if (read != interleaved.Length) break; // No zero-padding or invented trailing silence.
            var start = (double)consumed / rate;
            var end = (double)(consumed + size) / rate;
            for (var c = 0; c < processors.Length; c++)
            {
                for (var i = 0; i < size; i++) mono[i] = interleaved[i * channels + definition.Channels[c] - 1];
                var detection = processors[c].Process(mono, (long)Math.Round(end * 1000), definition.Settings);
                frames[c].Add(new(start, end, detection));
            }
            consumed += size;
        }
        if (consumed + size <= limit)
            throw new InvalidDataException("Decoded recording ended before the requested analysis range.");
        if (audioHash != Hash(audioPath) || sequenceHash != Hash(sequencePath) || caseHash != Hash(casePath))
            throw new IOException("Reference source changed during analysis; baseline discarded.");
        var results = frames.Select((f, i) => new { channel = definition.Channels[i], score = Score(definition, sequence, f) }).ToArray();
        // A fresh directory avoids replacing earlier evidence or accidentally writing over source materials.
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("Choose a new or empty reference output directory.");
        Directory.CreateDirectory(outputDirectory);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using (var reportFile = new FileStream(Path.Combine(outputDirectory, "report.json"), FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(reportFile, new
        {
            schemaVersion = 1, purpose = "Offline reference baseline, not a pass/fail hardware test",
            definition.Title, definition.Exercise, definition.TimingSource, definition.TimingUncertaintyMs,
            definition.AttackGuardMs, definition.ReleaseGuardMs, definition.PitchToleranceCents, definition.Settings,
            analysisEndSeconds = definition.AnalysisEndSeconds, sampleRate = rate, sourceChannels = channels, frameSamples = size,
            source = new { audioFile = Path.GetFileName(audioPath), audioHash, sequenceFile = Path.GetFileName(sequencePath), sequenceHash, caseHash,
                runnerAssemblyHash = Hash(typeof(ReferenceAudioEvaluation).Assembly.Location) },
            limitations = "Timings are reviewed local labels, not inferred with YIN. Count-in, boundaries, and unlabelled material are not steady-pitch ground truth. Accepted-event coverage uses approximate note intervals, not a measured onset-latency score. No WASAPI/device path, real-time scheduling, NUX signal, or technique validation.",
            results,
        }, options);
        for (var c = 0; c < frames.Length; c++)
        {
            using var csv = new StreamWriter(new FileStream(
                Path.Combine(outputDirectory, $"frames-channel-{definition.Channels[c]}.csv"), FileMode.CreateNew, FileAccess.Write));
            csv.WriteLine("startSeconds,endSeconds,midi,cents,hz,rms,peak,confidence,decision,acceptedCandidateMs");
            foreach (var frame in frames[c])
            {
                var d = frame.Detection;
                csv.WriteLine(FormattableString.Invariant($"{frame.StartSeconds:R},{frame.EndSeconds:R},{d.Midi},{d.Cents:R},{d.Frequency:R},{d.Rms:R},{d.Peak:R},{d.Confidence:R},{d.Decision},{d.OnsetTimestampMs}"));
            }
        }
        Console.WriteLine("REFERENCE BASELINE — measurement only, not a passing detector certification.");
        foreach (var result in results)
        {
            var s = result.score;
            Console.WriteLine(FormattableString.Invariant($"Channel {result.channel}: {s.SteadyFrames.WithinTolerance}/{s.SteadyFrames.Total} eligible steady frames within pitch tolerance; {s.SteadyFrames.Voiced} voiced; {s.RecoveredNotes}/{s.Notes.Length} notes had a usable accepted event; {s.OctaveErrorAcceptedEvents} accepted octave errors."));
        }
        Console.WriteLine($"Local report: {Path.Combine(outputDirectory, "report.json")}. No audio copied or uploaded.");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
