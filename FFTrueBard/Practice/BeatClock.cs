namespace FFTrueBard.Practice;

/// <summary>Software beat grid; timing is distinct from chord/pitch accuracy.</summary>
public sealed class BeatClock
{
    private readonly HashSet<long> scoredBeats = [];
    public bool Running { get; private set; }
    public double StartMs { get; private set; }
    public double PeriodMs { get; private set; } = 1000;
    public int ScoredNotes { get; private set; }
    public int OnTimeNotes { get; private set; }
    public double LastOffsetMs { get; private set; }
    private double absoluteTotal;
    public double MeanAbsoluteOffsetMs => ScoredNotes == 0 ? 0 : absoluteTotal / ScoredNotes;

    public void Start(long now, int bpm)
    {
        StartMs = now;
        PeriodMs = 60000.0 / Math.Clamp(bpm, 40, 160);
        Running = true;
        scoredBeats.Clear();
        ScoredNotes = OnTimeNotes = 0;
        LastOffsetMs = absoluteTotal = 0;
    }

    public void Stop() => Running = false;
    public long BeatAt(long now) => (long)Math.Floor((now - StartMs) / PeriodMs);
    public double PhaseAt(long now) => Math.Clamp((now - StartMs) / PeriodMs - BeatAt(now), 0, 1);

    public double? Observe(long detectedOnsetMs, int timingOffsetMs)
    {
        if (!Running)
            return null;
        var adjusted = detectedOnsetMs - Math.Clamp(timingOffsetMs, -250, 250);
        if (adjusted < StartMs + 4 * PeriodMs)
            return null;
        var beat = (long)Math.Round((adjusted - StartMs) / PeriodMs, MidpointRounding.AwayFromZero);
        if (beat < 4 || !scoredBeats.Add(beat))
            return null; // Four count-in beats; at most one timing score per beat.
        LastOffsetMs = adjusted - (StartMs + beat * PeriodMs);
        ScoredNotes++;
        absoluteTotal += Math.Abs(LastOffsetMs);
        if (Math.Abs(LastOffsetMs) <= 80)
            OnTimeNotes++;
        return LastOffsetMs;
    }
}

/// <summary>Deterministic sample-clock clicks; playback does not depend on render FPS.</summary>
public sealed class ClickSignal(int bpm)
{
    private long position;
    public const int SampleRate = 48000;

    public float Next()
    {
        var period = SampleRate * 60.0 / Math.Clamp(bpm, 40, 160);
        var beat = (long)Math.Floor(position / period);
        var offset = position++ - beat * period;
        if (offset >= 960)
            return 0;
        var frequency = beat % 4 == 0 ? 1600 : 1000;
        return (float)(0.12 * Math.Sin(2 * Math.PI * frequency * offset / SampleRate) * Math.Exp(-offset / 180));
    }
}
