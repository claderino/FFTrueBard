using FFTrueBard.Detection;

namespace FFTrueBard.Practice;

public sealed record LabelledTrial(string Expected, int ExpectedMidi, long WindowStartMs, long WindowEndMs,
    DetectionFrame[] Accepted, int Frames, int GatedFrames)
{
    public int Correct => Accepted.Count(f => f.Midi == ExpectedMidi);
    public int OctaveErrors => Accepted.Count(f => f.Midi != ExpectedMidi && (f.Midi - ExpectedMidi) % 12 == 0);
    public int WrongNotes => Accepted.Length - Correct - OctaveErrors;
    public int ExtraDetections => Math.Max(0, Accepted.Length - 1);
}

/// <summary>One prompted pick per two-second window, with silence before each trial.</summary>
public sealed class LabelledTest
{
    private readonly List<DetectionFrame> accepted = [];
    private int[] targets = [];
    private int current;
    private int frames;
    private int gatedFrames;
    private long windowStart;
    private long silenceSince;
    public List<LabelledTrial> Trials { get; } = [];
    public string Phase { get; private set; } = "idle";
    public bool Running => Phase is "mute" or "count-in" or "listen";
    public string Expected => current < targets.Length ? OpenStrings.Names[targets[current]] : "";
    public int TrialNumber => Math.Min(current + 1, targets.Length);
    public int TotalTrials => targets.Length;
    public double SecondsToCue(long now) => Math.Max(0, windowStart - now) / 1000.0;

    public void Start(int selectedString, int repeats, long now)
    {
        var strings = selectedString < 0 ? Enumerable.Range(0, 6) : [Math.Clamp(selectedString, 0, 5)];
        targets = strings.SelectMany(i => Enumerable.Repeat(i, Math.Clamp(repeats, 1, 10))).ToArray();
        Trials.Clear();
        current = 0;
        Phase = "mute";
        silenceSince = now;
        frames = gatedFrames = 0;
        accepted.Clear();
    }

    public void Cancel() => Phase = "cancelled";

    public void Receive(DetectionFrame frame)
    {
        if (!Running)
            return;
        if (Phase == "mute" && frame.Released && frame.TimestampMs >= silenceSince)
        {
            windowStart = frame.TimestampMs + 1000;
            Phase = "count-in";
        }
        if (Phase == "count-in" && frame.TimestampMs < windowStart && !frame.Released)
        {
            Phase = "mute"; // Early playing resets the cue, rather than scoring a held note.
            silenceSince = frame.TimestampMs;
            return;
        }
        if (frame.TimestampMs < windowStart || frame.TimestampMs >= windowStart + 2000 || Phase == "mute")
            return;
        Phase = "listen";
        frames++;
        if (frame.Decision == "gated")
            gatedFrames++;
        if (frame.Decision == "accepted" && accepted.Count < 64)
            accepted.Add(frame);
    }

    public void Tick(long now)
    {
        if (Phase == "mute" && now - silenceSince >= 15000)
        {
            Phase = "failed";
            return;
        }
        if (Phase == "count-in" && now >= windowStart)
            Phase = "listen";
        if (Phase != "listen" || now < windowStart + 2000)
            return;
        var target = targets[current];
        Trials.Add(new(OpenStrings.Names[target], OpenStrings.Midi[target], windowStart, windowStart + 2000,
            accepted.ToArray(), frames, gatedFrames));
        accepted.Clear();
        frames = gatedFrames = 0;
        current++;
        Phase = current == targets.Length ? "complete" : "mute";
        silenceSince = now;
    }
}
