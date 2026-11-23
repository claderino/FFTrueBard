using FFTrueBard.Detection;

namespace FFTrueBard.UI;

// Display history only: processed frames, never raw audio.
internal sealed class InputVisualizerHistory
{
    public const int Capacity = 120;
    private readonly Queue<DetectionFrame> frames = new(Capacity);
    private long lastTimestamp;

    public void Observe(DetectionFrame frame)
    {
        lock (frames)
        {
            if (frame.TimestampMs < lastTimestamp || frame.TimestampMs - lastTimestamp > 500)
                frames.Clear();
            lastTimestamp = frame.TimestampMs;
            if (frames.Count == Capacity) frames.Dequeue();
            frames.Enqueue(frame);
        }
    }

    public void Clear()
    {
        lock (frames)
        {
            frames.Clear();
            lastTimestamp = 0;
        }
    }

    public int CopyRecent(DetectionFrame[] target, long now)
    {
        lock (frames)
        {
            if (now - lastTimestamp > 500 || lastTimestamp > now + 100) return 0;
            frames.CopyTo(target, 0);
            return frames.Count;
        }
    }

    public static float? LevelFraction(float amplitude) => !float.IsFinite(amplitude) || amplitude < 0
        ? null : (Math.Clamp(20 * MathF.Log10(Math.Max(amplitude, 0.0001f)), -80, 0) + 80) / 80;

    public static float? PitchFraction(DetectionFrame frame)
    {
        if (frame.Midi is < 40 or > 87 || frame.Frequency <= 0 || !double.IsFinite(frame.Frequency) ||
            !double.IsFinite(frame.Cents) || !float.IsFinite(frame.Peak) || frame.Peak >= 0.98f ||
            !float.IsFinite(frame.Rms) || frame.Decision is "gated" or "unvoiced")
            return null;
        return (float)Math.Clamp((frame.Midi + frame.Cents / 100 - 40) / 47, 0, 1);
    }
}

internal static class LessonVisualizerPage
{
    // Follow the current target, or the last target once the run is finished.
    public static int PageAt(int noteIndex, int notes, int columns) =>
        Math.Clamp(noteIndex, 0, notes - 1) / columns;
}
