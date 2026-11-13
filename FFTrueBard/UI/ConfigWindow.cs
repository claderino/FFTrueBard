using System.Numerics;
using FFTrueBard.Link;
using Dalamud.Bindings.ImGui;

namespace FFTrueBard.UI;

public sealed class ConfigWindow
{
    private const string Title = "FFTrueBard Settings";

    private readonly Configuration config;
    private readonly NoteLinkServer server;
    private readonly object lockObj = new();
    private readonly List<string> recentNotes = [];
    private int portDraft;
    private bool visible;

    public ConfigWindow(Configuration config, NoteLinkServer server)
    {
        this.config = config;
        this.server = server;
        portDraft = config.ListenPort;
    }

    public bool Visible => visible;

    public void Open() => visible = true;

    public void Toggle() => visible = !visible;

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

        ImGui.SetNextWindowSize(new Vector2(430, 360), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Title, ref visible))
        {
            ImGui.End();
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled (inject notes)", ref enabled))
            config.Enabled = enabled;

        ImGui.InputInt("Detector port", ref portDraft);
        ImGui.TextDisabled("Changing the port needs a plugin reload.");
        if (portDraft != config.ListenPort)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply"))
                config.ListenPort = Math.Clamp(portDraft, 1024, 65535);
        }

        ImGui.Separator();
        ImGui.Text($"Detector clients connected: {server.ConnectedClients}");
        ImGui.TextDisabled("Start the detector:  python tools/detector/detector.py");

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
