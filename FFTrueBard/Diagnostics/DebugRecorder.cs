using System.Text.Json;
using System.Threading.Channels;
using FFTrueBard.Detection;

namespace FFTrueBard.Diagnostics;

/// <summary>Opt-in, local JSONL. Bounded queue; disk I/O never runs in an audio callback.</summary>
public sealed class DebugRecorder(string directory) : IDisposable
{
    private const long MaxBytes = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private Channel<object>? queue;
    private Task? writer;
    private volatile bool recording;
    private long dropped;
    private volatile string status = "Not recording";

    public bool Recording => recording;
    public string DirectoryPath => directory;
    public string LastPath { get; private set; } = "";
    public string Status => status;
    public long DroppedRows => Interlocked.Read(ref dropped);

    public void Start()
    {
        Stop();
        try
        {
            Directory.CreateDirectory(directory);
            LastPath = Path.Combine(directory, $"guitar-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            var stream = new FileStream(LastPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var pending = Channel.CreateBounded<object>(1024);
            queue = pending;
            Interlocked.Exchange(ref dropped, 0);
            pending.Writer.TryWrite(new { type = "session", schemaVersion = 1, startedUtc = DateTime.UtcNow,
                maxBytes = MaxBytes, note = "Frequency diagnostics only; no raw audio or game/chat data." });
            recording = true;
            status = "Recording (20 MiB limit per session)";
            writer = Task.Run(async () =>
            {
                try
                {
                    using (stream)
                    using (var output = new StreamWriter(stream))
                    {
                        var rows = 0;
                        await foreach (var row in pending.Reader.ReadAllAsync())
                        {
                            await output.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
                            if (++rows % 24 == 0)
                            {
                                await output.FlushAsync();
                                if (stream.Position >= MaxBytes)
                                {
                                    status = "Stopped: 20 MiB session limit reached. Start a new recording if needed.";
                                    recording = false;
                                    pending.Writer.TryComplete();
                                    break;
                                }
                            }
                        }
                        await output.WriteLineAsync(JsonSerializer.Serialize(new { type = "end", droppedRows = DroppedRows }, JsonOptions));
                    }
                }
                catch (Exception e)
                {
                    status = $"Log write failed: {e.Message}";
                    recording = false;
                    pending.Writer.TryComplete();
                }
            });
        }
        catch (Exception e)
        {
            recording = false;
            status = $"Cannot start recording: {e.Message}";
        }
    }

    public void Record(DetectionFrame frame, string device, int sampleRate, int inputChannel,
        string practiceExpected, string practiceFeedback, string mappedAction, long droppedAudioBlocks,
        float practiceToleranceCents = 35, long droppedDetectionFrames = 0)
    {
        if (!recording)
            return;
        var row = new { type = "frame", frame, device, sampleRate, inputChannel,
            practiceExpected, practiceFeedback, practiceToleranceCents, mappedAction,
            droppedAudioBlocks, droppedDetectionFrames };
        if (queue?.Writer.TryWrite(row) != true)
            Interlocked.Increment(ref dropped);
    }

    public void Stop()
    {
        recording = false;
        queue?.Writer.TryComplete();
        writer?.GetAwaiter().GetResult();
        writer = null;
        queue = null;
        if (status.StartsWith("Recording", StringComparison.Ordinal))
            status = "Saved";
    }

    public void Dispose() => Stop();
}
