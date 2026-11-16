using FFTrueBard.Detection;

namespace FFTrueBard.Practice;

public sealed record CalibrationSuggestion(float Noise95, float Soft25, float NormalMedian,
    float Gate, int StableFrames, string Warning);

/// <summary>Five-second measurements separated by explicit user-controlled pauses.</summary>
public sealed class CalibrationWizard
{
    private readonly List<DetectionFrame> idle = [];
    private readonly List<DetectionFrame> soft = [];
    private readonly List<DetectionFrame> normal = [];
    private long deadline;
    private long startedAt;
    public string Phase { get; private set; } = "idle";
    public string Status { get; private set; } = "Start with five seconds of silence: mute every string.";
    public CalibrationSuggestion? Suggestion { get; private set; }
    public bool Measuring => Phase is "noise" or "soft" or "normal";
    public bool Running => Measuring || Phase is "ready-soft" or "ready-normal";
    public double SecondsLeft(long now) => Math.Max(0, deadline - now) / 1000.0;

    public void Start(long now)
    {
        idle.Clear(); soft.Clear(); normal.Clear();
        Suggestion = null;
        Begin("noise", now);
    }

    public void Begin(string phase, long now)
    {
        if (phase != "noise" && !(phase == "soft" && Phase == "ready-soft") &&
            !(phase == "normal" && Phase == "ready-normal"))
            return;
        Phase = phase;
        startedAt = now;
        deadline = now + 5000;
        Status = phase == "noise" ? "Mute all strings; do not play." :
            $"Pick isolated E2 notes { (phase == "soft" ? "softly" : "at normal strength") }; mute between picks.";
    }

    public void Cancel()
    {
        Phase = "idle";
        Suggestion = null;
        Status = "Calibration cancelled. No settings changed.";
    }

    public void Receive(DetectionFrame frame)
    {
        if (!Measuring || frame.TimestampMs < startedAt || frame.TimestampMs >= deadline)
            return;
        var list = Phase == "noise" ? idle : Phase == "soft" ? soft : normal;
        if (list.Count < 1000 && float.IsFinite(frame.Rms) && float.IsFinite(frame.Peak))
            list.Add(frame);
    }

    public void Tick(long now)
    {
        if (!Measuring || now < deadline)
            return;
        var list = Phase == "noise" ? idle : Phase == "soft" ? soft : normal;
        if (list.Count < 20)
        {
            Phase = "failed";
            Status = "Not enough audio frames. Check your device, then restart calibration.";
            return;
        }
        if (Phase == "noise")
        {
            Phase = "ready-soft";
            Status = "Silence measured. When ready, record several soft E2 picks over five seconds.";
        }
        else if (Phase == "soft")
        {
            Phase = "ready-normal";
            Status = "Soft picks measured. Next, record several normal-strength E2 picks.";
        }
        else
            Finish();
    }

    private static float Percentile(IEnumerable<float> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * percentile)];
    }

    private void Finish()
    {
        Phase = "complete";
        var noise = Math.Max(0.00001f, Percentile(idle.Select(f => f.Rms), 0.95));
        var softSignal = soft.Where(f => f.Rms > Math.Max(noise * 2, 0.0002f)).ToArray();
        var normalSignal = normal.Where(f => f.Rms > Math.Max(noise * 2, 0.0002f)).ToArray();
        if (softSignal.Length < 8 || normalSignal.Length < 8)
        {
            Status = "Picks were not sufficiently above the noise floor. Check the channel/gain and repeat.";
            return;
        }
        var soft25 = Percentile(softSignal.Select(f => f.Rms), 0.25);
        if (soft25 < noise * 3)
        {
            Status = "Noise and soft notes overlap too much for a safe gate suggestion. Reduce noise and repeat.";
            return;
        }
        var voiced = softSignal.Concat(normalSignal).Where(f => f.Midi >= 0).ToArray();
        var changes = voiced.Zip(voiced.Skip(1)).Count(pair => pair.First.Midi != pair.Second.Midi);
        var stability = voiced.Length >= 8 && changes > voiced.Length * 0.1 ? 3 : 2;
        var warning = normal.Any(f => f.Peak >= 0.98f) ? "Clipping detected: lower input gain and repeat. " : "";
        if (voiced.Length < 8)
            warning += "Too few pitched frames to estimate stability; suggesting the default of 2. ";
        if (normalSignal.Count(f => f.Midi >= 0 && f.Midi != 40) > normalSignal.Length / 4)
            warning += "Many frames did not match E2; verify tuning and repeat with only the low E string.";
        var gate = Math.Clamp(MathF.Sqrt(noise * soft25), 0.0001f, 0.2f);
        if (gate <= noise || gate >= soft25)
        {
            Status = "Signal level is outside the supported gate range. Adjust input gain and repeat.";
            return;
        }
        Suggestion = new(noise, soft25, Percentile(normalSignal.Select(f => f.Rms), 0.5), gate, stability, warning);
        Status = "Review the suggestion. Nothing is applied until you choose Apply.";
    }
}
