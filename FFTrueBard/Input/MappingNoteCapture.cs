using FFTrueBard.Detection;

namespace FFTrueBard.Input;

/// <summary>Selects a draft note, never saves or sends an action.</summary>
public sealed class MappingNoteCapture
{
    private long startedAt;
    private long deadline;
    private bool waitingForMute;
    public bool Armed { get; private set; }
    public string Status { get; private set; } = "";

    public void Arm(long now)
    {
        startedAt = now;
        deadline = now + 15000;
        waitingForMute = true;
        Armed = true;
        Status = "Mute every string, then pick one note. This only selects the note; Save is still required.";
    }

    public string? Receive(DetectionFrame frame)
    {
        if (!Armed || frame.TimestampMs < startedAt)
            return null;
        Tick(frame.TimestampMs);
        if (!Armed)
            return null;
        if (waitingForMute)
        {
            if (frame.Released)
            {
                waitingForMute = false;
                Status = "Ready: pick one isolated note.";
            }
            return null;
        }
        if (frame.Decision != "accepted" || !MappingRules.TryNote(frame.Note, out var note, out _))
            return null;
        Armed = false;
        Status = $"Selected {note} ({frame.Frequency:0.00} Hz). Choose an action and Save binding.";
        return note;
    }

    public void Tick(long now)
    {
        if (Armed && now >= deadline)
        {
            Armed = false;
            Status = "Note selection timed out. Check capture/noise gate, then try again.";
        }
    }

    public void Cancel()
    {
        if (Armed)
            Status = "Note selection cancelled. No bindings changed.";
        Armed = false;
    }

    public void Reset()
    {
        Armed = false;
        Status = "";
    }
}
