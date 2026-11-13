using Dalamud.Configuration;

namespace FFTrueBard;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch. When off, notes are still displayed but not injected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TCP port the note detector connects to on localhost.</summary>
    public int ListenPort { get; set; } = 4242;

    /// <summary>
    /// Detected note name (e.g. "E2", "A3") to in-game action.
    /// Actions: "Mouse1", "Mouse2", "Mouse3", "Mouse4", "Mouse5" or "Key:Space", "Key:1" etc.
    /// Empty string = ignore.
    /// </summary>
    public Dictionary<string, string> Mapping { get; set; } = new()
    {
        // Open strings -> mouse buttons
        ["E2"] = "Mouse1",
        ["A2"] = "Mouse2",
        ["D3"] = "Mouse3",
        ["G3"] = "Mouse4",
        ["B3"] = "Mouse5",
        ["E4"] = "",

        // Fretted notes -> keys (placeholder defaults; edit in-game or here)
        ["F2"] = "Key:W",
        ["F#2"] = "Key:A",
        ["G2"] = "Key:S",
        ["G#2"] = "Key:D",
    };
}
