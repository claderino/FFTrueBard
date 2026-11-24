using System.Runtime.InteropServices;
using NAudio.Wave;
using SoundTouch;

namespace FFTrueBard.Lessons;

/// <summary>Stretches the decoded clip without resampling its pitch.</summary>
internal sealed class LessonTempoProvider(LoopingLessonStream source) : IWaveProvider
{
    internal const int Normal = 100;
    internal static int ClampPercent(int percent) => Math.Clamp(percent, 50, 150);
    internal static double ToMultiplier(int percent) => ClampPercent(percent) / 100d;

    private readonly object gate = new();
    private readonly byte[] input = new byte[1024 * source.WaveFormat.BlockAlign];
    private SoundTouchProcessor? processor;
    private bool ended;
    public WaveFormat WaveFormat => source.WaveFormat;
    public int SpeedPercent { get; private set; } = Normal;

    // Called on transport restart, including seek and tempo changes. The source already
    // enforces the exercise and loop bounds; resetting here also discards stretched data.
    public void Reset(double seconds, int percent)
    {
        lock (gate)
        {
            SpeedPercent = ClampPercent(percent);
            processor = SpeedPercent == Normal ? null : new SoundTouchProcessor
            {
                Channels = WaveFormat.Channels,
                SampleRate = WaveFormat.SampleRate,
                Tempo = ToMultiplier(SpeedPercent),
            };
            ended = false;
            source.SeekSeconds(seconds);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            if (processor is null) return source.Read(buffer, offset, count);
            var frameBytes = WaveFormat.BlockAlign;
            var total = 0;
            while (count - total >= frameBytes)
            {
                var destination = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset + total, count - total));
                var frames = processor.ReceiveSamples(destination, (count - total) / frameBytes);
                if (frames > 0)
                {
                    total += frames * frameBytes;
                    continue;
                }
                if (ended) break;
                var bytes = source.Read(input, 0, input.Length);
                if (bytes == 0)
                {
                    processor.Flush();
                    ended = true;
                    continue;
                }
                processor.PutSamples(MemoryMarshal.Cast<byte, float>(input.AsSpan(0, bytes)), bytes / frameBytes);
            }
            return total;
        }
    }

    internal static double SourceTime(double anchor, double elapsed, int speedPercent,
        double decoded, bool loop, double loopStart, double loopEnd)
    {
        var seconds = anchor + Math.Max(0, elapsed) * ToMultiplier(speedPercent);
        if (loop && loopEnd > loopStart)
            return seconds >= loopEnd ? loopStart + (seconds - loopEnd) % (loopEnd - loopStart) : seconds;
        return Math.Clamp(seconds, 0, decoded);
    }
}
