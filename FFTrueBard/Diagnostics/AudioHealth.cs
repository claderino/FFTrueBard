using FFTrueBard.Detection;

namespace FFTrueBard.Diagnostics;

public sealed record AudioHealthSnapshot(string State, long Blocks, long Samples, long ProcessedFrames,
    long AcceptedNotes, long ClippedFrames, long? LastBlockAgeMs, long? LastFrameAgeMs,
    float Rms, float Peak, long DroppedAudioBlocks, long DroppedDetectionFrames);

/// <summary>Capture-thread heartbeats separate real silence from absent audio callbacks.</summary>
public sealed class AudioHealth
{
    private int running;
    private long startedAt;
    private long lastBlock;
    private long blocks;
    private long samples;
    private long processed;
    private long accepted;
    private long clipped;
    private DetectionFrame? latest;

    public bool Running => Volatile.Read(ref running) != 0;

    public void Begin(long now)
    {
        Interlocked.Exchange(ref startedAt, now);
        Interlocked.Exchange(ref lastBlock, 0);
        Interlocked.Exchange(ref blocks, 0);
        Interlocked.Exchange(ref samples, 0);
        Interlocked.Exchange(ref processed, 0);
        Interlocked.Exchange(ref accepted, 0);
        Interlocked.Exchange(ref clipped, 0);
        Volatile.Write(ref latest, null);
        Volatile.Write(ref running, 1);
    }

    public void Stop() => Volatile.Write(ref running, 0);

    public void ObserveBlock(long now, int sampleCount)
    {
        if (sampleCount <= 0)
            return;
        Interlocked.Exchange(ref lastBlock, now);
        Interlocked.Increment(ref blocks);
        Interlocked.Add(ref samples, sampleCount);
    }

    public void ObserveFrame(DetectionFrame frame)
    {
        Volatile.Write(ref latest, frame);
        Interlocked.Increment(ref processed);
        if (frame.Decision == "accepted")
            Interlocked.Increment(ref accepted);
        if (frame.Peak >= 0.98f)
            Interlocked.Increment(ref clipped);
    }

    public AudioHealthSnapshot Snapshot(long now, long droppedAudio, long droppedFrames)
    {
        var blockAt = Interlocked.Read(ref lastBlock);
        var frame = Volatile.Read(ref latest);
        long? blockAge = blockAt == 0 ? null : Math.Max(0, now - blockAt);
        long? frameAge = frame is null ? null : Math.Max(0, now - frame.TimestampMs);
        var warmingUp = now - Interlocked.Read(ref startedAt) < 2000;
        var state = !Running ? "stopped" : blockAt == 0 ? warmingUp ? "starting" : "no-audio-callbacks" :
            blockAge > 1500 ? "capture-stalled" : frameAge is null ? warmingUp ? "starting" : "processing-stalled" :
            frameAge > 1500 ? "processing-stalled" : !float.IsFinite(frame!.Rms) || !float.IsFinite(frame.Peak) ? "invalid-samples" :
            frame.Peak >= 0.98f ? "clipping" : frame.Peak <= 0.000001f ? "silent-input" :
            frame.Rms < frame.Settings.RmsGate ? "below-noise-gate" : "signal-present";
        return new(state, Interlocked.Read(ref blocks), Interlocked.Read(ref samples), Interlocked.Read(ref processed),
            Interlocked.Read(ref accepted), Interlocked.Read(ref clipped), blockAge, frameAge,
            frame is not null && float.IsFinite(frame.Rms) ? frame.Rms : 0,
            frame is not null && float.IsFinite(frame.Peak) ? frame.Peak : 0, droppedAudio, droppedFrames);
    }
}
