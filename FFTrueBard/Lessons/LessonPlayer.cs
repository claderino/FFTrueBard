using NAudio;
using NAudio.Wave;

namespace FFTrueBard.Lessons;

/// <summary>Reads only the chosen MP3. Decoding/opening runs outside the game thread.</summary>
public sealed class LessonPlayer : IDisposable
{
    private Task<AudioFileReader>? loading;
    private WaveOutEvent? output;
    private LoopingLessonStream? stream;
    private LessonTempoProvider? tempo;
    private bool discardLoad;
    private bool disposed;
    private double positionAnchor;
    private long outputAnchor;
    private volatile bool outputStopped = true;
    private (double Seconds, bool Resume)? audibleSeek;
    public string Path { get; private set; } = "";
    public string Status { get; private set; } = "Select an MP3 track to load.";
    public bool Loading => loading is not null;
    public bool Ready => stream is not null;
    public bool Playing => audibleSeek?.Resume ?? (output?.PlaybackState == PlaybackState.Playing);
    public double Duration => stream?.TotalTime.TotalSeconds ?? 0;
    public double Position => stream?.Seconds ?? 0;
    // The decoder reads ahead. Use the frames reported by the output device for
    // the moving score marker rather than highlighting a buffered, unheard note.
    public double AudiblePosition
    {
        get
        {
            if (audibleSeek is { } seek) return seek.Seconds;
            if (stream is null || output is null || tempo is null || output.PlaybackState == PlaybackState.Stopped) return Position;
            try
            {
                var elapsed = (output.GetPosition() - outputAnchor) / (double)stream.WaveFormat.AverageBytesPerSecond;
                return LessonTempoProvider.SourceTime(positionAnchor, elapsed, tempo.SpeedPercent,
                    Position, LoopEnabled, LoopA, LoopB);
            }
            catch (InvalidOperationException) { return Position; }
            catch (MmException) { return Position; }
        }
    }
    public float Volume { get; set; } = 0.25f;
    public int SpeedPercent { get; private set; } = LessonTempoProvider.Normal;
    public bool LoopEnabled => stream?.LoopEnabled ?? false;
    public double LoopA => stream?.LoopA ?? 0;
    public double LoopB => stream?.LoopB ?? 0;

    public void Load(string path)
    {
        if (Loading || disposed) return;
        Unload();
        positionAnchor = outputAnchor = 0;
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
        if (audibleSeek is { } seek && outputStopped)
        {
            audibleSeek = null;
            RequestTransport(seek.Seconds, seek.Resume);
        }
        if (loading?.IsCompleted != true) return;
        try
        {
            var reader = loading.GetAwaiter().GetResult();
            if (discardLoad) { reader.Dispose(); Status = "Track loading cancelled. Select a track to load again."; return; }
            stream = new LoopingLessonStream(reader);
            tempo = new LessonTempoProvider(stream);
            tempo.Reset(0, SpeedPercent);
            output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
            output.Init(tempo);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Volume = Math.Clamp(Volume, 0, 1);
            Status = "Loaded. Playback uses the Windows default output. Press Play when ready.";
        }
        catch (Exception e) { Unload(); Status = $"Playback unavailable: {e.Message}"; }
        finally { loading = null; }
    }

    public void Play()
    {
        if (audibleSeek is { } seek)
        {
            audibleSeek = (seek.Seconds, true);
            return;
        }
        if (output is null || stream is null) return;
        try
        {
            if (output.PlaybackState == PlaybackState.Stopped)
            {
                var seconds = Position >= (stream.ClipEnd ?? Duration) - .01
                    ? (LoopEnabled ? LoopA : stream.ClipStart ?? 0) : Position;
                if (!outputStopped)
                {
                    audibleSeek = (seconds, true);
                    return;
                }
                if (Position != seconds) ApplySeek(seconds);
            }
            output.Volume = Math.Clamp(Volume, 0, 1);
            outputStopped = false;
            output.Play();
        }
        catch (Exception e)
        {
            outputStopped = output.PlaybackState == PlaybackState.Stopped;
            Status = $"Could not play: {e.Message}";
        }
    }

    public void Pause()
    {
        if (audibleSeek is { } seek) audibleSeek = (seek.Seconds, false);
        try { output?.Pause(); }
        catch (Exception e) { Status = $"Could not pause audio: {e.Message}"; }
    }
    public void Stop()
    {
        discardLoad = true;
        if (Loading) Status = "Cancelling track load...";
        RequestTransport(stream?.ClipStart ?? 0, resume: false);
    }

    // Restart only after WaveOut has acknowledged its stop. This clears queued audio
    // from before the scrub instead of showing a new fret while the old buffer plays.
    public void SeekFromScore(double seconds)
    {
        if (stream?.ClipStart is not null) Seek(seconds);
    }

    public bool SetScoreLoop(double a, double b)
    {
        if (stream?.SetClipLoop(a, b) != true) return false;
        Seek(a);
        return true;
    }

    public bool ClearScoreLoop()
    {
        var seconds = AudiblePosition;
        if (stream?.ClearClipLoop() != true) return false;
        Seek(seconds);
        return true;
    }

    private void RequestTransport(double seconds, bool resume)
    {
        if (stream is null || output is null || !double.IsFinite(seconds)) return;
        if (LoopEnabled && (seconds < LoopA || seconds >= LoopB)) seconds = LoopA;
        seconds = Math.Clamp(seconds, stream.ClipStart ?? 0, stream.ClipEnd ?? Duration);
        if (outputStopped)
        {
            if (audibleSeek is null)
            {
                try
                {
                    ApplySeek(seconds);
                    if (resume && Position < (stream.ClipEnd ?? Duration) - .01) Play();
                }
                catch (Exception e) { Status = $"Could not seek: {e.Message}"; }
            }
            else audibleSeek = (seconds, resume);
            return;
        }

        var stop = audibleSeek is null;
        audibleSeek = (seconds, resume);
        if (!stop) return;
        try { output.Stop(); }
        catch (Exception e)
        {
            audibleSeek = null;
            Status = $"Could not seek: {e.Message}";
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (ReferenceEquals(sender, output)) outputStopped = true;
    }
    public void SetSpeed(int percent)
    {
        percent = LessonTempoProvider.ClampPercent(percent);
        if (SpeedPercent == percent) return;
        var seconds = AudiblePosition;
        SpeedPercent = percent;
        RequestTransport(seconds, Playing);
    }

    public void Seek(double seconds) => RequestTransport(seconds, Playing);

    private void ApplySeek(double seconds)
    {
        if (stream is null || tempo is null) return;
        tempo.Reset(seconds, SpeedPercent);
        positionAnchor = stream.Seconds;
        try { outputAnchor = output?.GetPosition() ?? 0; }
        catch (InvalidOperationException) { outputAnchor = 0; }
        catch (MmException) { outputAnchor = 0; }
    }

    public bool SetPlaybackClip(double start, double end)
    {
        if (stream?.SetPlaybackClip(start, end) != true) return false;
        Seek(start);
        return true;
    }
    public bool SetLoop(double a, double b, bool enabled)
    {
        var seconds = AudiblePosition;
        if (stream?.SetLoop(a, b, enabled) != true) return false;
        Seek(seconds);
        return true;
    }
    public void ApplyVolume() { if (output is not null) output.Volume = Math.Clamp(Volume, 0, 1); }

    private void Unload()
    {
        audibleSeek = null;
        var old = output;
        output = null;
        if (old is not null)
        {
            old.PlaybackStopped -= OnPlaybackStopped;
            old.Dispose();
        }
        outputStopped = true;
        tempo = null;
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
    private (long Start, long End)? clip;
    public bool LoopEnabled { get; private set; }
    public double LoopA { get { lock (gate) return (double)loopStart / WaveFormat.AverageBytesPerSecond; } }
    public double LoopB { get { lock (gate) return (double)loopEnd / WaveFormat.AverageBytesPerSecond; } }
    public double Seconds { get { lock (gate) return source.CurrentTime.TotalSeconds; } }
    public double? ClipStart { get { lock (gate) return clip is { } c ? (double)c.Start / WaveFormat.AverageBytesPerSecond : null; } }
    public double? ClipEnd { get { lock (gate) return clip is { } c ? (double)c.End / WaveFormat.AverageBytesPerSecond : null; } }
    public override WaveFormat WaveFormat => source.WaveFormat;
    public override long Length => source.Length;
    public override long Position { get { lock (gate) return source.Position; } set { lock (gate) source.Position = Align(Math.Clamp(value, 0, Length)); } }
    private long Align(long value) => value - value % WaveFormat.BlockAlign;
    private long At(double seconds) => Align((long)(Math.Clamp(seconds, 0, TotalTime.TotalSeconds) * WaveFormat.AverageBytesPerSecond));
    public void SeekSeconds(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        lock (gate)
        {
            var requested = At(seconds);
            source.Position = clip is { } c ? Math.Clamp(requested, c.Start, c.End) : requested;
        }
    }

    public bool SetPlaybackClip(double start, double end)
    {
        if (!ValidRange(start, end)) return false;
        lock (gate)
        {
            clip = (At(start), At(end));
            LoopEnabled = false;
            source.Position = clip.Value.Start;
        }
        return true;
    }

    private bool ValidRange(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b <= TotalTime.TotalSeconds && b - a >= 0.1;

    public bool SetClipLoop(double a, double b)
    {
        if (!ValidRange(a, b)) return false;
        lock (gate)
        {
            var start = At(a);
            var end = At(b);
            if (clip is not { } c || start < c.Start || end > c.End || end <= start) return false;
            loopStart = start;
            loopEnd = end;
            LoopEnabled = true;
            return true;
        }
    }

    public bool ClearClipLoop()
    {
        lock (gate)
        {
            if (clip is null || !LoopEnabled) return false;
            LoopEnabled = false;
            return true;
        }
    }

    public bool SetLoop(double a, double b, bool enabled)
    {
        if (!ValidRange(a, b)) return false;
        lock (gate)
        {
            clip = null;
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
                var limit = LoopEnabled ? loopEnd : clip?.End ?? source.Length;
                var available = (int)Math.Min(count - total, limit - source.Position);
                if (available <= 0) break;
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
