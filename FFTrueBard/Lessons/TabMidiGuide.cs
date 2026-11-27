using System.Runtime.InteropServices;
using NAudio.Midi;
using NAudio.Wave;

namespace FFTrueBard.Lessons;

internal readonly record struct TabSynthNote(double Start, double End, int Pitch, int Velocity);

/// <summary>Renders a MIDI note guide to seekable float audio, without a MIDI device or soundfont.</summary>
internal static class TabMidiGuide
{
    private const int SampleRate = 32000;
    private const int MaximumFrames = 10 * 60 * SampleRate;
    private record struct Tempo(long Tick, double Seconds, double SecondsPerTick);
    private record struct Voice(int First, int Last, int Pitch, int Velocity);

    public static WaveStream Open(string path)
    {
        path = LessonLibrary.Normalize(path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".mid" or ".midi"))
            throw new InvalidDataException("Choose a .mid or .midi file.");
        using var input = File.OpenRead(path);
        if (input.Length is < 14 or > 1024 * 1024)
            throw new InvalidDataException("The MIDI guide must be between 14 bytes and 1 MiB.");

        // NAudio exposes the time division as an unsigned value. Reject SMPTE before parsing.
        Span<byte> header = stackalloc byte[14];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("MThd"u8) || (header[12] & 0x80) != 0)
            throw new InvalidDataException("Use a Standard MIDI file with beat-based timing.");
        input.Position = 0;
        var midi = new MidiFile(input, false);
        var division = midi.DeltaTicksPerQuarterNote;
        if (midi.FileFormat is not (0 or 1) || division <= 0)
            throw new InvalidDataException("Use a format 0 or 1 MIDI file with beat-based timing.");

        var events = Enumerable.Range(0, midi.Tracks).SelectMany(track => midi.Events[track]).ToArray();
        if (events.Length > 100_000 || events.Any(e => e.AbsoluteTime < 0))
            throw new InvalidDataException("The MIDI guide contains too many events or invalid times.");
        var tempos = new List<Tempo> { new(0, 0, .5 / division) };
        foreach (var change in events.OfType<TempoEvent>().OrderBy(e => e.AbsoluteTime))
        {
            if (change.MicrosecondsPerQuarterNote <= 0)
                throw new InvalidDataException("The MIDI guide has an invalid tempo.");
            var previous = tempos[^1];
            var next = new Tempo(change.AbsoluteTime, previous.Seconds + (change.AbsoluteTime - previous.Tick) * previous.SecondsPerTick,
                change.MicrosecondsPerQuarterNote / (1_000_000d * division));
            if (next.Tick == previous.Tick) tempos[^1] = next;
            else tempos.Add(next);
        }

        double Seconds(long tick)
        {
            var low = 0;
            var high = tempos.Count;
            while (high - low > 1)
            {
                var middle = (low + high) / 2;
                if (tempos[middle].Tick <= tick) low = middle;
                else high = middle;
            }
            var tempo = tempos[low];
            return tempo.Seconds + (tick - tempo.Tick) * tempo.SecondsPerTick;
        }

        var notes = new List<TabSynthNote>();
        foreach (var note in events.OfType<NoteOnEvent>())
        {
            if (note.Velocity == 0 || note.Channel == 10 || note.OffEvent is not { } off ||
                off.AbsoluteTime <= note.AbsoluteTime) continue;
            notes.Add(new(Seconds(note.AbsoluteTime), Seconds(off.AbsoluteTime), note.NoteNumber, note.Velocity));
        }
        return Render(notes, events.Length == 0 ? 0 : Seconds(events.Max(e => e.AbsoluteTime)));
    }

    internal static WaveStream Render(IEnumerable<TabSynthNote> notes, double duration)
    {
        static int Frame(double seconds)
        {
            var frames = seconds * SampleRate;
            if (!double.IsFinite(frames) || frames < 0 || frames > MaximumFrames)
                throw new InvalidDataException("The guide must be at most ten minutes long.");
            return (int)Math.Round(frames);
        }

        // Keep the declared end reachable for an A–B loop ending on the last attack.
        var end = Math.Min(MaximumFrames, Frame(duration) + 1);
        var voices = new List<Voice>();
        long work = 0;
        foreach (var note in notes)
        {
            if (note is not { Pitch: >= 0 and <= 127, Velocity: >= 1 and <= 127 })
                throw new InvalidDataException("The guide contains an out-of-range pitch or velocity.");
            var first = Frame(note.Start);
            var last = Math.Min(Frame(note.End), first + 4 * SampleRate);
            if (last <= first) continue;
            var stop = Math.Min(MaximumFrames, last + SampleRate / 20);
            work += stop - first;
            if (voices.Count >= 20_000 || work > 60_000_000)
                throw new InvalidDataException("The guide contains too many overlapping or sustained notes.");
            voices.Add(new(first, last, note.Pitch, note.Velocity));
            end = Math.Max(end, stop);
        }
        if (voices.Count == 0)
            throw new InvalidDataException("This guide has no playable pitched notes with durations.");

        var data = new byte[end * sizeof(float)];
        var samples = MemoryMarshal.Cast<byte, float>(data.AsSpan());
        Span<float> delay = stackalloc float[4000];
        var random = new Random(0);
        foreach (var voice in voices)
        {
            var frequency = 440 * Math.Pow(2, (voice.Pitch - 69) / 12d);
            var period = SampleRate / frequency;
            var length = Math.Max(2, (int)Math.Ceiling(period));
            var blend = (float)(length - period);
            for (var i = 0; i < length; i++)
                delay[i] = random.NextSingle() * 2 - 1;

            // Low-pass feedback gives a plucked-string decay; the short release keeps note-offs click-free.
            var amplitude = voice.Velocity * (.34f / 127);
            var damping = MathF.Pow(.45f, length / (float)SampleRate);
            var index = 0;
            for (var frame = voice.First; frame < end && frame < voice.Last + SampleRate / 20; frame++)
            {
                var next = index + 1 == length ? 0 : index + 1;
                var value = delay[index];
                delay[index] = (value + (delay[next] - value) * blend) * damping;
                index = next;
                var attack = Math.Min(1f, (frame - voice.First) / (SampleRate / 500f));
                var release = 1f - Math.Max(0, frame - voice.Last) / (SampleRate / 20f);
                samples[frame] += value * (amplitude * attack * release);
            }
        }
        for (var i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i], -.95f, .95f);
        return new RawSourceWaveStream(new MemoryStream(data, writable: false), WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
    }
}
