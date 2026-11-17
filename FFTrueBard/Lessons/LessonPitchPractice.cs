using FFTrueBard.Detection;

namespace FFTrueBard.Lessons;

/// <summary>Untimed, mute-separated pitch order. No attack, rhythm, or fingering claims.</summary>
public sealed class LessonPitchPractice
{
    private long startedAt;
    private long lastFrameAt;
    private long lastSeenAt;
    private long armedAt;
    public LessonSequence? Sequence { get; private set; }
    public bool Running { get; private set; }
    public bool WaitingForMute { get; private set; }
    public int Index { get; private set; }
    public int Correct { get; private set; }
    public int Wrong { get; private set; }
    public long UncertainFrames { get; private set; }
    public int Skipped => Results.Count(r => r == "skipped");
    public string[] Results { get; private set; } = [];
    public int[] Retries { get; private set; } = [];
    public float CentsTolerance { get; private set; }
    public string State { get; private set; } = "idle";
    public string Feedback { get; private set; } = "Load and review a local pitch sequence to begin.";
    public string Outcome { get; private set; } = "Not started";
    public SequenceNote? Expected => Sequence is not null && Index < Sequence.Notes.Length ? Sequence.Notes[Index] : null;
    public bool Finished => Sequence is not null && Index == Sequence.Notes.Length;

    public void Start(LessonSequence sequence, long now, float tolerance)
    {
        sequence.Validate(sequence.Exercise);
        // A running session is a snapshot; a later file edit cannot change its targets.
        Sequence = sequence with { Notes = sequence.Notes.ToArray() };
        Results = Enumerable.Repeat("waiting", sequence.Notes.Length).ToArray();
        Retries = new int[sequence.Notes.Length];
        Index = Correct = Wrong = 0;
        UncertainFrames = 0;
        startedAt = lastFrameAt = lastSeenAt = now;
        CentsTolerance = float.IsFinite(tolerance) ? Math.Clamp(tolerance, 5, 50) : 35;
        WaitingForMute = Running = true;
        State = "waiting";
        Outcome = "Running";
        Feedback = "Mute all strings before the first pick. Play slowly, one note at a time.";
    }

    public void Receive(DetectionFrame frame, long now)
    {
        if (!Running || frame.TimestampMs <= lastSeenAt) return;
        lastSeenAt = frame.TimestampMs;
        if (frame.TimestampMs < startedAt || now - frame.TimestampMs > 500 || frame.TimestampMs > now + 100)
        {
            Uncertain("Old or out-of-clock frame ignored; mute before trying again.");
            WaitingForMute = true;
            return;
        }
        lastFrameAt = frame.TimestampMs;
        if (!float.IsFinite(frame.Rms) || !float.IsFinite(frame.Peak) || !float.IsFinite(frame.Confidence) ||
            !double.IsFinite(frame.Frequency) || !double.IsFinite(frame.Cents) || frame.Peak >= 0.98f)
        {
            Uncertain("Uncertain / clipped input — not scored. Check gain, then mute and retry.");
            WaitingForMute = true;
            return;
        }
        if (WaitingForMute)
        {
            if (frame.Decision == "gated" && frame.Released && frame.Rms < frame.Settings.RmsGate)
            {
                WaitingForMute = false;
                armedAt = frame.TimestampMs;
                // Keep a scored result visible through the mute, until the next pick.
                if (State is not ("correct" or "wrong")) State = "ready";
                Feedback = $"Ready: pick {Expected!.Name}.";
            }
            return;
        }
        if (frame.Decision == "gated") return;
        if (frame.Decision != "accepted")
        {
            if (frame.Decision is "unvoiced" or "stabilizing" or "cooldown")
                Uncertain("Listening for a stable single-note event — not scored.");
            return;
        }
        if (frame.Confidence < 0.8f || frame.Midi is < 40 or > 87 || frame.Frequency <= 0 ||
            frame.Rms < frame.Settings.RmsGate || frame.Released ||
            (frame.OnsetTimestampMs ?? frame.TimestampMs) <= armedAt ||
            frame.OnsetTimestampMs > frame.TimestampMs)
        {
            Uncertain("Uncertain pitch event — not scored. Mute and retry.");
            WaitingForMute = true;
            return;
        }
        WaitingForMute = true;
        var target = Expected!;
        if (frame.Midi != target.Midi || Math.Abs(frame.Cents) > CentsTolerance)
        {
            Wrong++;
            Retries[Index]++;
            State = "wrong";
            Feedback = $"Wrong pitch: heard {SequenceNote.NoteName(frame.Midi)} ({frame.Cents:+0.0;-0.0;0} cents); expected {target.Name}. Mute, then retry this note.";
            return;
        }
        Correct++;
        Results[Index++] = "matched";
        State = "correct";
        Feedback = $"{target.Name} matched. Mute before the next pick.";
        FinishIfNeeded();
    }

    private void Uncertain(string text)
    {
        UncertainFrames++;
        State = "uncertain";
        Feedback = text;
    }

    public void Skip()
    {
        if (!Running) return;
        Results[Index++] = "skipped";
        WaitingForMute = true;
        State = "waiting";
        Feedback = "Expected note explicitly skipped (not a detected miss). Mute for the next pick.";
        FinishIfNeeded();
    }

    private void FinishIfNeeded()
    {
        if (!Finished) return;
        Running = false;
        Outcome = "Sequence ended";
        Feedback = $"Sequence ended: {Correct} matched; {Skipped} explicitly skipped; {Wrong} wrong-pitch attempts. Rhythm and technique were not checked.";
    }

    public void Stop(string reason = "Stopped. Results retained until the next start.")
    {
        if (!Running) return;
        Running = false;
        State = "uncertain";
        Outcome = "Stopped";
        Feedback = reason;
    }

    public void Tick(long now)
    {
        if (Running && now - lastFrameAt > 1500)
            Stop("No recent detector frames — grading stopped without adding mistakes. Check capture, then restart this drill.");
    }
}
