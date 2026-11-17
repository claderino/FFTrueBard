using Dalamud.Configuration;

namespace FFTrueBard;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;

    // Local-only media locations. Never included in diagnostic snapshots or release assets.
    public string LessonRoot { get; set; } = "";
    public string LessonPdf { get; set; } = "";
    public string LessonExercisesFolder { get; set; } = "";
    public string LessonRhythmFolder { get; set; } = "";
    public int LessonExercise { get; set; } = 1;
    public float LessonVolume { get; set; } = 0.25f;
    public string LessonSequenceFile { get; set; } = "";
    public float LessonPitchTolerance { get; set; } = 35;

    public bool PracticeMode { get; set; } = true;
    public int PracticeChord { get; set; }
    public int TransitionChord { get; set; } = 1;
    public bool AlternateChords { get; set; }
    public float PracticeCentsTolerance { get; set; } = 35;
    public int MetronomeBpm { get; set; } = 60;
    public bool AudibleMetronome { get; set; }
    public int TimingOffsetMs { get; set; }

    /// <summary>Master switch. When off, notes are still displayed but not injected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Substring to match against capture device friendly names (e.g. "NUX").
    /// Empty = use the Windows default capture device.
    /// </summary>
    public string DeviceName { get; set; } = "NUX";
    public int InputChannel { get; set; }

    /// <summary>Detected note name (e.g. "E2", "A3") to in-game action.</summary>
    /// Actions: "Mouse1".."Mouse5" or "Key:Space", "Key:Ctrl+Shift+1", ... Empty string = ignore.
    public Dictionary<string, string> Mapping { get; set; } = Input.MappingRules.DefaultMappings();

    // Detection tuning (mirrors the old detector defaults)
    public float DetectionThreshold { get; set; } = 0.15f;
    public float RmsGate { get; set; } = 0.004f;
    public int RetriggerMs { get; set; } = 150;
    public int StableFrames { get; set; } = 2;
    public int ReleaseMs { get; set; } = 100;

    public Diagnostics.EditableSettings CaptureEditable()
    {
        var detection = GetDetectionSettings();
        return new(DeviceName ?? "", Math.Clamp(InputChannel, 0, 7), detection.Threshold, detection.RmsGate,
            detection.StableFrames, detection.RetriggerMs, detection.ReleaseMs, Enabled, new(Mapping));
    }

    public void RestoreEditable(Diagnostics.EditableSettings settings)
    {
        if (!settings.IsValid)
            throw new InvalidDataException("Unsupported recovery settings.");
        DeviceName = settings.DeviceName;
        InputChannel = settings.InputChannel;
        DetectionThreshold = settings.Threshold;
        RmsGate = settings.Gate;
        StableFrames = settings.StableFrames;
        RetriggerMs = settings.RetriggerMs;
        ReleaseMs = settings.ReleaseMs;
        Enabled = settings.MappingEnabled;
        Mapping = new(settings.Mapping);
    }

    public Detection.DetectionSettings GetDetectionSettings() => new(
        Math.Clamp(DetectionThreshold, 0.02f, 0.5f), Math.Clamp(RmsGate, 0.0001f, 0.2f),
        Math.Clamp(StableFrames, 1, 10), Math.Clamp(RetriggerMs, 0, 1000),
        Math.Clamp(ReleaseMs, 40, 1000));
}
