using NAudio.Wave;

namespace FFTrueBard.Lessons;

/// <summary>Reads only the chosen MP3. Decoding/opening runs outside the game thread.</summary>
public sealed class LessonPlayer : IDisposable
{
    private Task<AudioFileReader>? loading;
    private WaveOutEvent? output;
    private LoopingLessonStream? stream;
    private bool discardLoad;
    private bool disposed;
    public string Path { get; private set; } = "";
    public string Status { get; private set; } = "Select an MP3 track to load.";
    public bool Loading => loading is not null;
    public bool Ready => stream is not null;
    public bool Playing => output?.PlaybackState == PlaybackState.Playing;
    public double Duration => stream?.TotalTime.TotalSeconds ?? 0;
    public double Position => stream?.Seconds ?? 0;
    public float Volume { get; set; } = 0.25f;
    public bool LoopEnabled => stream?.LoopEnabled ?? false;
    public double LoopA => stream?.LoopA ?? 0;
    public double LoopB => stream?.LoopB ?? 0;

    public void Load(string path)
    {
        if (Loading || disposed) return;
        Unload();
        Path = path;
        discardLoad = false;
        Status = "Loading local MP3...";
        loading = Task.Run(() =>
        {
            var full = LessonLibrary.Normalize(path);
            if (!System.IO.Path.GetExtension(full).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a local .mp3 file.");
            return new AudioFileReader(full);
        });
    }

    public void Update()
    {
        if (loading?.IsCompleted != true) return;
        try
        {
            var reader = loading.GetAwaiter().GetResult();
            if (discardLoad) { reader.Dispose(); Status = "Track loading cancelled. Select a track to load again."; return; }
            stream = new LoopingLessonStream(reader);
            output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
            output.Init(stream);
            output.Volume = Math.Clamp(Volume, 0, 1);
            Status = "Loaded. Playback uses the Windows default output. Press Play when ready.";
        }
        catch (Exception e) { Unload(); Status = $"Playback unavailable: {e.Message}"; }
        finally { loading = null; }
    }

    public void Play()
    {
        if (output is null || stream is null) return;
        try
        {
            if (Position >= Duration - 0.01) Seek(LoopEnabled ? LoopA : 0);
            output.Volume = Math.Clamp(Volume, 0, 1);
            output.Play();
        }
        catch (Exception e) { Status = $"Could not play: {e.Message}"; }
    }

    public void Pause()
    {
        try { output?.Pause(); }
        catch (Exception e) { Status = $"Could not pause audio: {e.Message}"; }
    }
    public void Stop()
    {
        discardLoad = true;
        if (Loading) Status = "Cancelling track load...";
        try
        {
            output?.Stop();
            stream?.SeekSeconds(0);
        }
        catch (Exception e) { Status = $"Could not stop audio: {e.Message}. Reload the track or check the output device."; }
    }
    public void Seek(double seconds) => stream?.SeekSeconds(seconds);
    public bool SetLoop(double a, double b, bool enabled) => stream?.SetLoop(a, b, enabled) ?? false;
    public void ApplyVolume() { if (output is not null) output.Volume = Math.Clamp(Volume, 0, 1); }

    private void Unload()
    {
        output?.Dispose();
        output = null;
        stream?.Dispose();
        stream = null;
    }

    public void Dispose()
    {
        disposed = true;
        Unload();
        if (loading is not null)
        {
            _ = loading.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                else _ = task.Exception;
            }, TaskScheduler.Default);
            loading = null;
        }
    }
}

/// <summary>Loop boundaries applied by the audio reader, not by UI frame timing.</summary>
public sealed class LoopingLessonStream(WaveStream source) : WaveStream
{
    private readonly object gate = new();
    private long loopStart;
    private long loopEnd = source.Length;
    public bool LoopEnabled { get; private set; }
    public double LoopA { get { lock (gate) return (double)loopStart / WaveFormat.AverageBytesPerSecond; } }
    public double LoopB { get { lock (gate) return (double)loopEnd / WaveFormat.AverageBytesPerSecond; } }
    public double Seconds { get { lock (gate) return source.CurrentTime.TotalSeconds; } }
    public override WaveFormat WaveFormat => source.WaveFormat;
    public override long Length => source.Length;
    public override long Position { get { lock (gate) return source.Position; } set { lock (gate) source.Position = Align(Math.Clamp(value, 0, Length)); } }
    private long Align(long value) => value - value % WaveFormat.BlockAlign;
    private long At(double seconds) => Align((long)(Math.Clamp(seconds, 0, TotalTime.TotalSeconds) * WaveFormat.AverageBytesPerSecond));
    public void SeekSeconds(double seconds) { if (double.IsFinite(seconds)) { lock (gate) source.Position = At(seconds); } }

    public bool SetLoop(double a, double b, bool enabled)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b > TotalTime.TotalSeconds || b - a < 0.1) return false;
        lock (gate)
        {
            loopStart = At(a);
            loopEnd = At(b);
            LoopEnabled = enabled;
            if (enabled && (source.Position < loopStart || source.Position >= loopEnd)) source.Position = loopStart;
        }
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            var total = 0;
            while (total < count)
            {
                if (LoopEnabled && (source.Position >= loopEnd || source.Position < loopStart)) source.Position = loopStart;
                var available = LoopEnabled ? (int)Math.Min(count - total, loopEnd - source.Position) : count - total;
                var read = source.Read(buffer, offset + total, available);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { lock (gate) source.Dispose(); }
        base.Dispose(disposing);
    }
}
