using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private static float ReaderScale => ImGui.GetFontSize() / 17f;
    private static Vector4 Rgb(uint rgb) =>
        new((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f, 1f);

    private static readonly Vector4 ReaderGold = Rgb(0xe1bf83);
    private static readonly Vector4 ReaderBrass = Rgb(0x332c22);
    private static readonly Vector4 ReaderPaper = Rgb(0xf5eedc);
    private static readonly string[] Workspaces = ["Lessons", "Practice", "Input", "Settings"];
    private string activeWorkspace = "Lessons";

    // This palette applies only to FFTrueBard. The host font, UI scale, and window state still work normally.
    private readonly struct ReaderStyle : IDisposable
    {
        private static readonly (ImGuiCol Slot, Vector4 Color)[] Palette =
        [
            (ImGuiCol.WindowBg, Rgb(0x12161a)),
            (ImGuiCol.ChildBg, Vector4.Zero),
            (ImGuiCol.PopupBg, Rgb(0x191e24)),
            (ImGuiCol.Text, Rgb(0xf3efe7)),
            (ImGuiCol.TextDisabled, Rgb(0xaab3c0)),
            (ImGuiCol.TitleBg, Rgb(0x24282d)),
            (ImGuiCol.TitleBgActive, Rgb(0x24282d)),
            (ImGuiCol.TitleBgCollapsed, Rgb(0x24282d)),
            (ImGuiCol.Border, Rgb(0x45403a)),
            (ImGuiCol.Separator, Rgb(0x373d46)),
            (ImGuiCol.Tab, Vector4.Zero),
            (ImGuiCol.TabUnfocused, Vector4.Zero),
            (ImGuiCol.TabActive, ReaderBrass),
            (ImGuiCol.TabUnfocusedActive, ReaderBrass),
            (ImGuiCol.TabHovered, Rgb(0x36313a)),
            (ImGuiCol.Button, Rgb(0x222831)),
            (ImGuiCol.ButtonHovered, Rgb(0x3a4049)),
            (ImGuiCol.ButtonActive, Rgb(0x4e4537)),
            (ImGuiCol.FrameBg, Rgb(0x222831)),
            (ImGuiCol.FrameBgHovered, Rgb(0x333b46)),
            (ImGuiCol.FrameBgActive, Rgb(0x453b30)),
            (ImGuiCol.Header, Rgb(0x222831)),
            (ImGuiCol.HeaderHovered, Rgb(0x333b46)),
            (ImGuiCol.HeaderActive, Rgb(0x453b30)),
            (ImGuiCol.CheckMark, ReaderGold),
            (ImGuiCol.SliderGrab, ReaderGold),
            (ImGuiCol.SliderGrabActive, Rgb(0xf1d8ad)),
            (ImGuiCol.NavHighlight, ReaderGold),
        ];

        public ReaderStyle(float scale)
        {
            foreach (var (slot, color) in Palette) ImGui.PushStyleColor(slot, color);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 5) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7 * scale);
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(Palette.Length);
        }
    }

    private static float ReaderButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + 2 * ImGui.GetStyle().FramePadding.X;

    private static bool ReaderNavButton(string label, bool active, bool underline)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, active ? ReaderGold : Rgb(0xb7c0cc));
        ImGui.PushStyleColor(ImGuiCol.Button, active && !underline ? ReaderBrass : Vector4.Zero);
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(2);
        if (active && underline)
        {
            var start = ImGui.GetItemRectMin();
            var end = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(new(start.X, end.Y), end, ImGui.GetColorU32(ReaderGold), 2 * ReaderScale);
        }
        return pressed;
    }

    private static void ContinueReaderRow(float right, float nextWidth)
    {
        var end = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetScrollX();
        if (end + ImGui.GetStyle().ItemSpacing.X + nextWidth <= right) ImGui.SameLine();
    }

    private static bool AlignReaderAction(float right, float controlsWidth)
    {
        var end = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetScrollX();
        if (end + ImGui.GetStyle().ItemSpacing.X + controlsWidth > right) return false;
        ImGui.SameLine(right - controlsWidth);
        return true;
    }

    private void DrawWorkspaceNavigation()
    {
        activeWorkspace = WorkspaceForTool(requestedTab) ?? activeWorkspace;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var mode = config.PracticeMode ? "Practice mode" : "Mapping preview";
        for (var i = 0; i < Workspaces.Length; i++)
        {
            var label = Workspaces[i];
            if (i > 0) ContinueReaderRow(right, ReaderButtonWidth(label));
            if (ReaderNavButton(label, label == activeWorkspace, underline: true)) activeWorkspace = label;
        }
        AlignReaderAction(right, ReaderButtonWidth(mode));
        DrawPracticeModeControl(mode);
        ImGui.Separator();
    }

    private void DrawPracticeModeControl(string label)
    {
        var practiceMode = config.PracticeMode;
        ImGui.PushStyleColor(ImGuiCol.Text, practiceMode ? Rgb(0xa8cdae) : ReaderGold);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button($"{label}###practice_toggle");
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(practiceMode
                ? "Practice mode is on. Click to switch to mapping preview. No keyboard or mouse input is sent."
                : "Mapping preview only. Click to return to Practice mode. No keyboard or mouse input is sent.");
        if (!clicked) return;
        config.PracticeMode = !practiceMode;
        StopTools();
        ResetPractice();
        save();
    }
}
