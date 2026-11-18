namespace FFTrueBard.Detection;

public sealed record DetectionSettings(float Threshold, float RmsGate, int StableFrames,
    int RetriggerMs, int ReleaseMs);

public sealed record DetectionFrame(long TimestampMs, double Frequency, string Note, int Midi,
    double Cents, float Rms, float Peak, float Confidence, string Decision, bool Released,
    DetectionSettings Settings, long? OnsetTimestampMs = null, bool FreshAttack = false);

/// <summary>Audio-only state, owned by one capture worker. No game or UI calls.</summary>
public sealed class PitchProcessor(int sampleRate)
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private int candidate = -1;
    private int agreeingFrames;
    private int lastAccepted = -1;
    private double quietMs;
    private long lastAcceptedAt = long.MinValue / 2;
    private long candidateStartedAt;
    private readonly PickAttackDetector attacks = new(sampleRate);
    private long attackAt = long.MinValue / 2;
    private long consumedAttackAt = long.MinValue / 2;

    public DetectionFrame Process(float[] samples, long timestampMs, DetectionSettings settings)
    {
        double squares = 0;
        float peak = 0;
        foreach (var sample in samples)
        {
            squares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        var rms = (float)Math.Sqrt(squares / samples.Length);
        var newAttack = attacks.Process(samples, timestampMs, settings);
        if (newAttack is { } onset)
        {
            attackAt = onset;
            candidate = -1;
            agreeingFrames = 0;
        }
        // Attack evidence expires if no stable pitch follows, and can justify only one acceptance.
        var freshAttack = attackAt > consumedAttackAt && timestampMs - attackAt <= 250;
        var acceptedAttack = false;
        var freq = 0f;
        var confidence = 0f;
        var midi = -1;
        var note = "";
        double cents = 0;
        string decision;
        if (rms < settings.RmsGate || !float.IsFinite(rms))
        {
            quietMs += samples.Length * 1000.0 / sampleRate;
            candidate = -1;
            agreeingFrames = 0;
            if (quietMs >= settings.ReleaseMs)
                lastAccepted = -1;
            decision = "gated";
        }
        else
        {
            quietMs = 0;
            (freq, confidence) = YinPitch.Analyze(samples, sampleRate, 70, 1300, settings.Threshold);
            if (freq <= 0)
            {
                candidate = -1;
                agreeingFrames = 0;
                decision = "unvoiced";
            }
            else
            {
                var exactMidi = 69 + 12 * Math.Log2(freq / 440.0);
                midi = (int)Math.Round(exactMidi);
                cents = (exactMidi - midi) * 100;
                note = $"{Names[midi % 12]}{midi / 12 - 1}";
                if (midi != candidate)
                    candidateStartedAt = timestampMs;
                agreeingFrames = midi == candidate ? agreeingFrames + 1 : 1;
                candidate = midi;
                if (agreeingFrames < settings.StableFrames)
                    decision = "stabilizing";
                else if (lastAccepted == midi && !freshAttack)
                    decision = "sustained";
                else if (timestampMs - lastAcceptedAt < settings.RetriggerMs)
                    decision = "cooldown";
                else
                {
                    decision = "accepted";
                    lastAccepted = midi;
                    lastAcceptedAt = timestampMs;
                    acceptedAttack = freshAttack;
                    consumedAttackAt = attackAt;
                }
            }
        }

        return new DetectionFrame(timestampMs, freq, note, midi, cents, rms, peak,
            confidence, decision, quietMs >= settings.ReleaseMs, settings,
            midi >= 0 ? candidateStartedAt : null, acceptedAttack);
    }
}
