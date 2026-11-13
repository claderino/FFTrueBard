using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dalamud.Plugin.Services;

namespace FFTrueBard.Link;

/// <summary>A single note event produced by the external detector.</summary>
public sealed record NoteEvent(string Note, double Freq, float Amp, long TimestampMs);

/// <summary>
/// Tiny localhost TCP server. The Python detector streams JSON lines:
///   {"note": "E2", "freq": 82.41, "amp": 0.42, "t": 1699999999999}
/// </summary>
public sealed class NoteLinkServer : IDisposable
{
    private readonly Channel<NoteEvent> events =
        Channel.CreateUnbounded<NoteEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IPluginLog log;
    private readonly int port;
    private TcpListener listener = null!;
    private CancellationTokenSource? cts;
    private volatile int connectedClients;

    public NoteLinkServer(int port, IPluginLog log)
    {
        this.port = port;
        this.log = log;
    }

    public ChannelReader<NoteEvent> Events => events.Reader;
    public int ConnectedClients => connectedClients;
    public int Port => port;

    public void Start()
    {
        cts = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (Exception e)
        {
            log.Error($"FFTrueBard: could not listen on 127.0.0.1:{port} - {e.Message}");
            return;
        }

        log.Info($"FFTrueBard: listening for the note detector on 127.0.0.1:{port}");
        _ = Task.Run(() => AcceptLoopAsync(cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            Interlocked.Increment(ref connectedClients);
            _ = Task.Run(() => ReadLoopAsync(client, ct));
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                Handle(line);
            }
        }
        catch
        {
            // client dropped or socket error - the accept loop will take new clients
        }
        finally
        {
            Interlocked.Decrement(ref connectedClients);
        }
    }

    private void Handle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var note = root.TryGetProperty("note", out var n) ? n.GetString() ?? "?" : "?";
            var freq = root.TryGetProperty("freq", out var f) ? f.GetDouble() : 0.0;
            var amp = root.TryGetProperty("amp", out var a) ? (float)a.GetDouble() : 0f;
            var t = root.TryGetProperty("t", out var ts) && ts.TryGetInt64(out var v)
                ? v
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            events.Writer.TryWrite(new NoteEvent(note, freq, amp, t));
        }
        catch
        {
            // ignore malformed lines from the detector
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        try
        {
            listener.Stop();
        }
        catch
        {
        }
    }
}
