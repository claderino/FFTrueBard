using Dalamud.Configuration;

namespace FFTrueBard;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>Master switch. When off, notes are still displayed but not injected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Substring to match against capture device friendly names (e.g. "NUX").
    /// Empty = use the Windows default capture device.
    /// </summary>
    public string DeviceName { get; set; } = "NUX";

    /// <summary>Detected note name (e.g. "E2", "A3") to in-game action.</summary>
    /// Actions: "Mouse1".."Mouse5" or "Key:Space", "Key:1", ... Empty string = ignore.
    public Dictionary<string, string> Mapping { get; set; } = new()
    {
        // Open strings -> mouse buttons
        ["E2"] = "Mouse1",
        ["A2"] = "Mouse2",
        ["D3"] = "Mouse3",
        ["G3"] = "Mouse4",
        ["B3"] = "Mouse5",
        ["E4"] = "",

        // Fretted notes -> keys (placeholder defaults; edit here for now)
        ["F2"] = "Key:W",
        ["F#2"] = "Key:A",
        ["G2"] = "Key:S",
        ["G#2"] = "Key:D",
    };

    // Detection tuning (mirrors the old detector defaults)
    public float DetectionThreshold { get; set; } = 0.15f;
    public float RmsGate { get; set; } = 0.004f;
    public int RetriggerMs { get; set; } = 150;
    public int StableFrames { get; set; } = 2;
}
