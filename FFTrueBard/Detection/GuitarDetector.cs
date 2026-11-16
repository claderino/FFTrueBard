using System.Threading.Channels;
using Dalamud.Plugin.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FFTrueBard.Detection;

/// <summary>WASAPI capture and pitch processing off the game thread.</summary>
public sealed class GuitarDetector(Configuration config, IPluginLog log) : IDisposable
{
    private readonly Channel<DetectionFrame> frames = Channel.CreateBounded<DetectionFrame>(128);
    private DetectionSettings settings = config.GetDetectionSettings();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private WasapiCapture? capture;
    private MMDevice? device;
    private long droppedAudioBlocks;
    private long droppedDetectionFrames;

    public ChannelReader<DetectionFrame> Frames => frames.Reader;
    public string StatusText { get; private set; } = "Not started";
    public string ActiveDevice { get; private set; } = "";
    public int SampleRate { get; private set; }
    public long DroppedAudioBlocks => Interlocked.Read(ref droppedAudioBlocks);
    public long DroppedDetectionFrames => Interlocked.Read(ref droppedDetectionFrames);
    public int ActiveChannel { get; private set; }
    public IReadOnlyList<string> DeviceNames { get; private set; } = [];

    public void ApplySettings() => Volatile.Write(ref settings, config.GetDetectionSettings());

    public void RefreshDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                    names.Add(endpoint.FriendlyName);
            }
            DeviceNames = names;
        }
        catch (Exception e)
        {
            StatusText = $"Could not list devices: {e.Message}";
            log.Error(e, "FFTrueBard: device enumeration failed.");
        }
    }

    public void Start()
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (string.IsNullOrWhiteSpace(config.DeviceName))
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            else
            {
                foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (device is null && endpoint.FriendlyName.Contains(config.DeviceName, StringComparison.OrdinalIgnoreCase))
                        device = endpoint;
                    else
                        endpoint.Dispose();
                }
            }

            if (device is null)
            {
                StatusText = $"No input matching '{config.DeviceName}'. Select your guitar interface below.";
                return; // Never silently record a different microphone.
            }

            capture = new WasapiCapture(device);
            var format = capture.WaveFormat;
            var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            var pcm = format.Encoding == WaveFormatEncoding.Pcm ||
                format is WaveFormatExtensible pcmExt && pcmExt.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
            if ((!floating || format.BitsPerSample != 32) &&
                (!pcm || format.BitsPerSample is not (16 or 24 or 32)))
                throw new NotSupportedException($"Unsupported input format: {format}");

            ActiveDevice = device.FriendlyName;
            SampleRate = format.SampleRate;
            var channel = Math.Clamp(config.InputChannel, 0, format.Channels - 1);
            ActiveChannel = channel + 1;
            var blocks = Channel.CreateBounded<float[]>(4);
            var source = new CancellationTokenSource();
            cancellation = source;
            var rate = SampleRate;
            capture.DataAvailable += (_, e) =>
            {
                if (source.IsCancellationRequested)
                    return;
                var mono = new float[e.BytesRecorded / format.BlockAlign];
                var bytesPerSample = format.BitsPerSample / 8;
                for (var i = 0; i < mono.Length; i++)
                {
                    var offset = i * format.BlockAlign + channel * bytesPerSample;
                    mono[i] = floating ? BitConverter.ToSingle(e.Buffer, offset) : format.BitsPerSample switch
                    {
                        16 => BitConverter.ToInt16(e.Buffer, offset) / 32768f,
                        24 => ((e.Buffer[offset] | e.Buffer[offset + 1] << 8 | e.Buffer[offset + 2] << 16) << 8 >> 8) / 8388608f,
                        _ => BitConverter.ToInt32(e.Buffer, offset) / 2147483648f,
                    };
                }
                if (!blocks.Writer.TryWrite(mono))
                    Interlocked.Increment(ref droppedAudioBlocks);
            };
            capture.RecordingStopped += (_, e) =>
            {
                source.Cancel();
                if (e.Exception is not null)
                {
                    StatusText = $"Capture stopped: {e.Exception.Message}";
                    log.Error(e.Exception, "FFTrueBard: audio capture stopped.");
                }
            };
            worker = Task.Run(() => DetectionLoop(blocks.Reader, rate, source.Token));
            capture.StartRecording();
            StatusText = $"Capturing '{ActiveDevice}' at {rate} Hz, channel {channel + 1}/{format.Channels}";
        }
        catch (Exception e)
        {
            Stop();
            StatusText = $"Capture unavailable: {e.Message}";
            log.Error(e, "FFTrueBard: audio initialization failed. Select a device and retry.");
        }
    }

    private async Task DetectionLoop(ChannelReader<float[]> blocks, int rate, CancellationToken ct)
    {
        try
        {
            var processor = new PitchProcessor(rate);
            var frame = new float[Math.Max(1024, (int)(2048L * rate / 48000))];
            var count = 0;
            await foreach (var block in blocks.ReadAllAsync(ct))
            {
                foreach (var sample in block)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    frame[count++] = sample;
                    if (count != frame.Length)
                        continue;
                    if (!frames.Writer.TryWrite(processor.Process(frame,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Volatile.Read(ref settings))))
                        Interlocked.Increment(ref droppedDetectionFrames);
                    count = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            StatusText = $"Detection stopped: {e.Message}";
            log.Error(e, "FFTrueBard: pitch processing failed.");
        }
    }

    private void Stop()
    {
        cancellation?.Cancel();
        capture?.Dispose(); // Stops the capture thread before disposing its token source.
        capture = null;
        worker?.GetAwaiter().GetResult();
        worker = null;
        cancellation?.Dispose();
        cancellation = null;
        device?.Dispose();
        device = null;
        while (frames.Reader.TryRead(out _)) { }
        ActiveDevice = "";
        SampleRate = 0;
        ActiveChannel = 0;
        StatusText = "Stopped";
    }

    public void Dispose() => Stop();
}
