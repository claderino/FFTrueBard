using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Detection;

namespace FFTrueBard.UI;

public sealed class ConfigWindow
{
    private const string Title = "FFTrueBard Settings";

    private readonly Configuration config;
    private readonly GuitarDetector detector;
    private readonly object lockObj = new();
    private readonly List<string> recentNotes = [];

    private string[] devices = [];
    private bool visible;

    public ConfigWindow(Configuration config, GuitarDetector detector)
    {
        this.config = config;
        this.detector = detector;
        devices = [.. detector.DeviceNames];
    }

    public bool Visible => visible;

    public void Open()
    {
        visible = true;
        detector.RefreshDevices();
        devices = [.. detector.DeviceNames];
    }

    public void Toggle()
    {
        if (visible)
            visible = false;
        else
            Open();
    }

    public void PushEvent(string note, float amp)
    {
        lock (lockObj)
        {
            recentNotes.Insert(0, $"{note}   amp {amp:0.00}");
            if (recentNotes.Count > 100)
                recentNotes.RemoveAt(100);
        }
    }

    public void Draw()
    {
        if (!visible)
            return;

        ImGui.SetNextWindowSize(new Vector2(430, 380), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Title, ref visible))
        {
            ImGui.End();
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled (inject notes)", ref enabled))
            config.Enabled = enabled;

        ImGui.Separator();

        // Device picker
        var selected = config.DeviceName;
        if (string.IsNullOrWhiteSpace(selected))
            selected = "(default capture device)";
        if (ImGui.BeginCombo("Input device", selected))
        {
            if (ImGui.Selectable("(default capture device)", string.IsNullOrWhiteSpace(config.DeviceName)))
            {
                config.DeviceName = "";
                detector.Restart();
            }

            foreach (var name in devices)
            {
                if (ImGui.Selectable(name, name == config.DeviceName))
                {
                    config.DeviceName = name;
                    detector.StartWith(name);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
        {
            detector.RefreshDevices();
            devices = [.. detector.DeviceNames];
        }

        ImGui.Text($"Status: {detector.StatusText}");
        ImGui.TextDisabled("Tip: set the NUX USB routing to Dry Out for a clean signal.");

        ImGui.Separator();
        ImGui.Text(
            $"Detection: threshold {config.DetectionThreshold:0.00}, gate {config.RmsGate:0.000}, " +
            $"retrigger {config.RetriggerMs} ms, stable {config.StableFrames}");
        ImGui.TextDisabled("Tune these in Configuration.cs for now; in-game editing is on the roadmap.");

        ImGui.Separator();
        ImGui.TextUnformatted("Recent notes (newest on top)");
        ImGui.BeginChild("recent_notes", new Vector2(0, 0));
        lock (lockObj)
        {
            foreach (var line in recentNotes)
                ImGui.TextUnformatted(line);
        }

        ImGui.EndChild();

        ImGui.End();
    }
}
