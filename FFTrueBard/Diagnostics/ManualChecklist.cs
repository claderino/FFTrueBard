using FFTrueBard.Detection;

namespace FFTrueBard.Diagnostics;

public sealed class ChecklistStep
{
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Result { get; set; } = "Not tested";
    public string Notes { get; set; } = "";
    public long Frames { get; set; }
    public long GatedFrames { get; set; }
    public long ClippedFrames { get; set; }
    public Dictionary<string, int> AcceptedNotes { get; set; } = [];
}

/// <summary>Human-labelled observations, never automatic claims that hardware passed.</summary>
public sealed class ManualChecklist
{
    public bool Running { get; private set; }
    public long StartedMs { get; private set; }
    public long? FinishedMs { get; private set; }
    public int Index { get; private set; }
    public IReadOnlyList<ChecklistStep> Steps { get; private set; } = CreateSteps();
    public ChecklistStep Current => Steps[Index];
    public string Id { get; private set; } = "";
    public string Outcome { get; private set; } = "Not started";

    private static ChecklistStep[] CreateSteps() =>
    [
        new() { Name = "Silence", Instructions = "Mute every string for five seconds. Audio callbacks must keep arriving; check the idle RMS, gate, clipping, and drops." },
        new() { Name = "Six open strings", Instructions = "Pick E2, A2, D3, G3, B3, E4 separately, muting between picks. Compare note names, octaves, and cents in Tuner." },
        new() { Name = "Repeated picks", Instructions = "Run five labelled low-E picks in Detection test. Check no-detection trials, extra events, wrong octaves, and dropped frames." },
        new() { Name = "One chord", Instructions = "Run one Em shape in Chord practice. Pick each prompted string and mute between picks. Mark problems with expected pitches or advancement." },
        new() { Name = "Mapping preview", Instructions = "Inspect a saved note binding and its log-only resolution. No actual FFXIV keys or clicks are sent; this tests detection and configuration only." },
    ];

    public void Start(long now)
    {
        Steps = CreateSteps();
        Index = 0;
        StartedMs = now;
        FinishedMs = null;
        Id = Guid.NewGuid().ToString("N");
        Running = true;
        Outcome = "Running";
    }

    public void Observe(DetectionFrame frame)
    {
        if (!Running || frame.TimestampMs < StartedMs)
            return;
        Current.Frames++;
        if (frame.Decision == "gated") Current.GatedFrames++;
        if (frame.Peak >= 0.98f) Current.ClippedFrames++;
        if (frame.Decision == "accepted" && Current.AcceptedNotes.Count < 128)
            Current.AcceptedNotes[frame.Note] = Current.AcceptedNotes.GetValueOrDefault(frame.Note) + 1;
    }

    public bool SetResult(string result, string notes)
    {
        if (!Running || result is not ("Pass" or "Issue" or "Skipped"))
            return false;
        Current.Result = result;
        UpdateNotes(notes);
        return true;
    }

    public void UpdateNotes(string notes) => Current.Notes = notes.Length > 2000 ? notes[..2000] : notes;

    public bool Next(long now)
    {
        if (!Running || Current.Result == "Not tested")
            return false;
        if (Index + 1 < Steps.Count)
            Index++;
        else
        {
            FinishedMs = now;
            Running = false;
            Outcome = "Completed";
        }
        return true;
    }

    public void Cancel(long now)
    {
        if (!Running)
            return;
        Running = false;
        Outcome = "Cancelled";
        FinishedMs = now;
    }
}
