using NAudio.Wave;

namespace FFTrueBard.Practice;

/// <summary>Quiet generated clicks, using the Windows default playback device.</summary>
public sealed class MetronomePlayer : IDisposable
{
    private WaveOutEvent? output;
    public string Error { get; private set; } = "";

    public void Start(int bpm)
    {
        Stop();
        try
        {
            output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 2 };
            output.Init(new ClickProvider(bpm).ToWaveProvider());
            output.Play();
            Error = "";
        }
        catch (Exception e)
        {
            Stop();
            Error = $"Audio click unavailable: {e.Message}. Visual metronome still works.";
        }
    }

    private sealed class ClickProvider(int bpm) : ISampleProvider
    {
        private readonly ClickSignal signal = new(bpm);
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(ClickSignal.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
                buffer[offset + i] = signal.Next();
            return count;
        }
    }

    public void Stop()
    {
        output?.Dispose();
        output = null;
    }

    public void Dispose() => Stop();
}
