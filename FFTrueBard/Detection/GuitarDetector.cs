using System.Runtime.InteropServices;
using System.Threading.Channels;
using Dalamud.Plugin.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FFTrueBard.Detection;

/// <summary>A single detected note event.</summary>
public sealed record NoteEvent(string Note, double Freq, float Amp, long TimestampMs);

/// <summary>
/// Self-contained guitar detector: WASAPI capture (NAudio) -> YIN pitch
/// detection -> note events. Replaces the old external Python detector.
/// </summary>
public sealed class GuitarDetector : IDisposable
{
    private const int SampleRate = 48000;
    private const int FrameLen = 2048; // ~43 ms
    private const float FMin = 70f;    // low E-ish (E2 = 82.4 Hz)
    private const float FMax = 1300f;
    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly Channel<float[]> rawBlocks =
        Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<NoteEvent> notes =
        Channel.CreateUnbounded<NoteEvent>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? cts;
    private WasapiCapture? capture;
    private MMDevice? device;

    public GuitarDetector(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    /// <summary>Detected notes for the game-thread pump.</summary>
    public ChannelReader<NoteEvent> Notes => notes.Reader;

    public string StatusText { get; private set; } = "Not started";

    /// <summary>Friendly names of active capture devices (for the device picker).</summary>
    public IReadOnlyList<string> DeviceNames { get; private set; } = [];

    public void RefreshDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        DeviceNames = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToArray();
    }

    /// <summary>Start capturing, resolving the device from the config (substring match, else default).</summary>
    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? dev = null;
        var wanted = config.DeviceName;

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            dev = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        dev ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        Start(dev);
    }

    public void Start(MMDevice dev)
    {
        Stop();
        device = dev;

        cts = new CancellationTokenSource();
        capture = new WasapiCapture(dev)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1),
        };
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        _ = Task.Run(() => DetectionLoop(cts.Token));
        capture.StartRecording();
        StatusText = $"Capturing '{dev.FriendlyName}' @ {SampleRate} Hz";
        log.Info($"FFTrueBard: {StatusText}");
    }

    /// <summary>Restart with the device currently configured (used when the user picks a new device).</summary>
    public void Restart() => Start();

    /// <summary>Start capturing from the device with this exact friendly name.</summary>
    public void StartWith(string friendlyName)
    {
        var enumerator = new MMDeviceEnumerator();
        var dev = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName == friendlyName);
        if (dev is null)
        {
            log.Warning($"FFTrueBard: device '{friendlyName}' not found; falling back.");
            Start();
            return;
        }

        Start(dev);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var floats = new float[e.BytesRecorded / sizeof(float)];
        Marshal.Copy(e.Buffer, floats, 0, floats.Length);
        rawBlocks.Writer.TryWrite(floats);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        StatusText = e.Exception is null ? "Stopped" : $"Stopped: {e.Exception.Message}";
        if (e.Exception is not null)
            log.Error($"FFTrueBard: capture stopped - {e.Exception}");
    }

    private void DetectionLoop(CancellationToken ct)
    {
        var pending = new float[1 << 16];
        var count = 0;
        var frame = new float[FrameLen];
        var stable = Math.Max(1, config.StableFrames);
        var candidates = new Queue<string>();
        string? lastNote = null;
        double lastSentMs = 0;

        while (!ct.IsCancellationRequested)
        {
            float[] block;
            try
            {
                block = rawBlocks.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Append block to the pending buffer, growing if needed.
            if (count + block.Length > pending.Length)
            {
                var bigger = new float[Math.Max(pending.Length * 2, count + block.Length)];
                Array.Copy(pending, bigger, count);
                pending = bigger;
            }

            Array.Copy(block, 0, pending, count, block.Length);
            count += block.Length;

            // Process complete frames.
            while (count >= FrameLen)
            {
                Array.Copy(pending, frame, FrameLen);
                Array.Copy(pending, FrameLen, pending, 0, count - FrameLen);
                count -= FrameLen;

                DetectFrame(frame, stable, candidates, ref lastNote, ref lastSentMs);
            }
        }
    }

    private void DetectFrame(float[] frame, int stable, Queue<string> candidates,
        ref string? lastNote, ref double lastSentMs)
    {
        // Amplitude gate - skip DSP while silent.
        float sumSquares = 0f;
        for (var i = 0; i < frame.Length; i++)
            sumSquares += frame[i] * frame[i];
        var rms = MathF.Sqrt(sumSquares / frame.Length);
        if (rms < config.RmsGate)
        {
            candidates.Clear();
            return;
        }

        var freq = YinPitch.Detect(frame, SampleRate, FMin, FMax, config.DetectionThreshold);
        if (freq <= 0f)
            return;

        var midi = 69.0 + 12.0 * Math.Log2(freq / 440.0);
        var m = (int)Math.Round(midi);
        var note = $"{NoteNames[m % 12]}{m / 12 - 1}";

        candidates.Enqueue(note);
        while (candidates.Count > stable)
            candidates.Dequeue();
        if (candidates.Count(n => n == note) < stable)
            return;

        var nowMs = (double)Environment.TickCount64;
        if (note == lastNote && nowMs - lastSentMs < config.RetriggerMs)
            return;

        lastNote = note;
        lastSentMs = nowMs;
        notes.Writer.TryWrite(new NoteEvent(note, Math.Round(freq, 2), rms, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void Dispose()
    {
        cts?.Cancel();
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.StopRecording();
            }
            catch
            {
            }

            capture.Dispose();
            capture = null;
        }

        device?.Dispose();
        device = null;
    }
}
