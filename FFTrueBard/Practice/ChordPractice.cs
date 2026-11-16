using FFTrueBard.Detection;

namespace FFTrueBard.Practice;

public sealed record ChordShape(string Name, int[] Frets, string[] Fingers)
{
    private static readonly int[] OpenMidi = [40, 45, 50, 55, 59, 64];
    public int MidiAt(int stringIndex) => OpenMidi[stringIndex] + Frets[stringIndex];
    public string NoteAt(int stringIndex)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        var midi = MidiAt(stringIndex);
        return Frets[stringIndex] < 0 ? "mute" : $"{names[midi % 12]}{midi / 12 - 1}";
    }
}

/// <summary>Standard tuning, no capo; X strings cannot be assessed from silence.</summary>
public static class Chords
{
    public static readonly ChordShape[] All =
    [
        new("Em", [0, 2, 2, 0, 0, 0], ["-", "2", "3", "-", "-", "-"]),
        new("Am", [-1, 0, 2, 2, 1, 0], ["X", "-", "2", "3", "1", "-"]),
        new("C", [-1, 3, 2, 0, 1, 0], ["X", "3", "2", "-", "1", "-"]),
        new("G", [3, 2, 0, 0, 0, 3], ["2", "1", "-", "-", "-", "3"]),
        new("D", [-1, -1, 0, 2, 3, 2], ["X", "X", "-", "1", "3", "2"]),
        new("E", [0, 2, 2, 1, 0, 0], ["-", "2", "3", "1", "-", "-"]),
        new("A", [-1, 0, 2, 2, 2, 0], ["X", "-", "1", "2", "3", "-"]),
        new("Dm", [-1, -1, 0, 2, 3, 1], ["X", "X", "-", "2", "3", "1"]),
    ];
}

/// <summary>Consumes confirmed single notes only. UI/game thread owns all state.</summary>
public sealed class ChordPractice
{
    private int first;
    private int second = 1;
    private bool alternate;
    private int active;
    private long? checkStartedAt;

    public ChordPractice() => Configure(0, 1, false);
    public ChordShape Chord => Chords.All[active];
    public int StringIndex { get; private set; }
    public bool WaitingForMute { get; private set; }
    public bool Complete { get; private set; }
    public int CompletedChecks { get; private set; }
    public int Mistakes { get; private set; }
    public double LastCheckSeconds { get; private set; }
    public string Feedback { get; private set; } = "";
    public string Expected => Complete ? "complete" : $"{Chord.Name}: string {6 - StringIndex}, {Chord.NoteAt(StringIndex)}";

    public void Configure(int firstChord, int secondChord, bool alternating)
    {
        first = Math.Clamp(firstChord, 0, Chords.All.Length - 1);
        second = Math.Clamp(secondChord, 0, Chords.All.Length - 1);
        alternate = alternating && first != second;
        CompletedChecks = 0;
        Mistakes = 0;
        LastCheckSeconds = 0;
        active = first;
        BeginCheck(null);
    }

    public void Retry() => BeginCheck(null);

    private void BeginCheck(long? startedAt)
    {
        StringIndex = Array.FindIndex(Chord.Frets, fret => fret >= 0);
        WaitingForMute = true;
        Complete = false;
        checkStartedAt = startedAt;
        Feedback = "Hold the shape, then mute all strings before the first pick.";
    }

    public void Receive(DetectionFrame frame, float centsTolerance)
    {
        if (Complete)
            return;
        if (WaitingForMute)
        {
            if (frame.Released)
            {
                WaitingForMute = false;
                checkStartedAt ??= frame.TimestampMs;
                Feedback = $"Pick {Expected}. Let only that string ring.";
            }
            return;
        }
        if (frame.Decision != "accepted")
            return;

        if (frame.Midi != Chord.MidiAt(StringIndex) || Math.Abs(frame.Cents) > centsTolerance)
        {
            Mistakes++;
            Feedback = $"Heard {frame.Note} ({frame.Cents:+0.0;-0.0;0} cents). Expected {Chord.NoteAt(StringIndex)}. Mute and retry this string.";
            WaitingForMute = true;
            return;
        }

        var passed = Chord.NoteAt(StringIndex);
        var next = StringIndex + 1;
        while (next < 6 && Chord.Frets[next] < 0)
            next++;
        WaitingForMute = true;
        if (next < 6)
        {
            StringIndex = next;
            Feedback = $"{passed} passed. Mute all strings, then pick {Expected}.";
            return;
        }

        CompletedChecks++;
        LastCheckSeconds = Math.Max(0, frame.TimestampMs - checkStartedAt.GetValueOrDefault(frame.TimestampMs)) / 1000.0;
        var finished = Chord.Name;
        if (alternate)
        {
            active = active == first ? second : first;
            BeginCheck(frame.TimestampMs);
            Feedback = $"{finished} checked! Change to {Chord.Name}; mute, then pick low to high.";
        }
        else
        {
            Complete = true;
            Feedback = $"{finished}: all expected notes passed! Strum for your own listening, or retry.";
        }
    }
}
