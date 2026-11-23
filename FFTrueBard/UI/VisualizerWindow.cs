using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Detection;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private static readonly (int Midi, string Name)[] PitchGuides = [(40, "E2"), (52, "E3"), (64, "E4"), (76, "E5")];
    private readonly InputVisualizerHistory inputVisualizer = new();
    private readonly DetectionFrame[] visualizerFrames = new DetectionFrame[InputVisualizerHistory.Capacity];
    private LessonSequence? visualizedRunSource;
    private int lessonVisualPage;

    private void DrawInputVisualizer(SequenceNote? target = null)
    {
        var count = detector.IsCapturing ? inputVisualizer.CopyRecent(visualizerFrames, Now) : 0;
        if (count == 0)
        {
            ImGui.TextUnformatted("No recent input frames. Check the selected guitar device and channel.");
            return;
        }

        var width = ImGui.GetContentRegionAvail().X;
        var font = ImGui.GetFontSize();
        var labelWidth = font * 4.2f;
        if (width < labelWidth + 32)
        {
            ImGui.TextUnformatted("Widen the window to see the graph.");
            return;
        }

        var frame = visualizerFrames[count - 1];
        var currentNote = frame.Note.Length > 0 ? frame.Note : "--";
        var header = $"Now: {currentNote} | {frame.Decision} | RMS {frame.Rms:0.0000} | Peak {frame.Peak:0.000}";
        ImGui.TextWrapped(target is null ? header : $"{header} | Current target: {target.Name}");
        if (frame.Peak >= 0.98f) ImGui.TextWrapped("Clipping: lower input gain.");

        var origin = ImGui.GetCursorScreenPos();
        var left = origin.X + labelWidth;
        var right = origin.X + width;
        var levelTop = origin.Y + font * 0.5f;
        var levelBottom = levelTop + font * 3;
        var pitchTop = levelBottom + font * 1.7f;
        var pitchBottom = pitchTop + font * 5;
        var draw = ImGui.GetWindowDrawList();
        var grid = ImGui.GetColorU32(ImGuiCol.Border);
        var labels = ImGui.GetColorU32(ImGuiCol.Text);
        var background = ImGui.GetColorU32(ImGuiCol.FrameBg);
        var level = ImGui.GetColorU32(ImGuiCol.PlotLines);
        var gate = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.76f, 0.30f, 1f));
        var estimate = ImGui.ColorConvertFloat4ToU32(new Vector4(0.54f, 0.73f, 0.99f, 1f));
        var accepted = ImGui.ColorConvertFloat4ToU32(new Vector4(0.39f, 0.97f, 0.66f, 1f));
        var clip = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.48f, 0.39f, 1f));
        draw.AddRectFilled(new Vector2(left, levelTop - 4), new Vector2(right, levelBottom + 4), background);
        draw.AddRectFilled(new Vector2(left, pitchTop - 4), new Vector2(right, pitchBottom + 4), background);
        void GridLine(float y, string name)
        {
            draw.AddText(new Vector2(origin.X, y - font * 0.5f), labels, name);
            draw.AddLine(new Vector2(left, y), new Vector2(right, y), grid, 1);
        }
        GridLine(levelTop, "0 dBFS");
        GridLine(levelBottom, "-80");
        foreach (var (midi, name) in PitchGuides)
            GridLine(PitchY((midi - 40) / 47f), name);

        var step = Math.Max(1, right - left - 10) / (InputVisualizerHistory.Capacity - 1);
        if (InputVisualizerHistory.LevelFraction(frame.Settings.RmsGate) is { } gateLevel)
        {
            var y = LevelY(gateLevel);
            for (var x = left + 2; x < right - 2; x += 10)
                draw.AddLine(new Vector2(x, y), new Vector2(Math.Min(x + 5, right - 2), y), gate, 1);
        }
        Vector2? previousLevel = null;
        for (var i = 0; i < count; i++)
        {
            var sample = visualizerFrames[i];
            var x = right - 5 - (count - i - 1) * step;
            if (InputVisualizerHistory.LevelFraction(sample.Rms) is { } strength)
            {
                var point = new Vector2(x, LevelY(strength));
                if (previousLevel is { } prior) draw.AddLine(prior, point, level, 1.5f);
                else draw.AddCircleFilled(point, 1.5f, level);
                previousLevel = point;
            }
            else previousLevel = null;
            if (float.IsFinite(sample.Peak) && sample.Peak >= 0.98f)
                draw.AddLine(new Vector2(x, levelTop), new Vector2(x, levelTop + 5), clip, 2);
            if (InputVisualizerHistory.PitchFraction(sample) is { } pitch)
            {
                var point = new Vector2(x, PitchY(pitch));
                if (sample.Decision != "accepted") draw.AddCircleFilled(point, 1.6f, estimate);
                else if (sample.FreshAttack)
                    draw.AddRectFilled(point - new Vector2(3), point + new Vector2(3), accepted);
                else draw.AddCircleFilled(point, 3.4f, accepted);
            }
        }
        ImGui.Dummy(new Vector2(width, pitchBottom - origin.Y + 7));
        ImGui.TextWrapped("RMS line | Dashed = current gate | Top ticks = clipping\n" +
            "Small dot = pitch estimate | Large dot = accepted | Square = accepted with fresh-pick evidence");
        ImGui.TextWrapped("Last 120 delivered detector frames, newest at right. This is not a waveform or a timing score.");

        float LevelY(float value) => levelBottom - value * (levelBottom - levelTop);
        float PitchY(float value) => pitchBottom - value * (pitchBottom - pitchTop);
    }

    private void DrawLessonVisualizer()
    {
        if (!ImGui.CollapsingHeader("Exercise & input visualizer", ImGuiTreeNodeFlags.DefaultOpen)) return;
        SequenceNote? target = null;
        if (SelectedCatalogExercise?.Mode != "guided" &&
            pitchSequence is { } sequence && sequence.Exercise == config.LessonExercise)
        {
            var currentRun = ReferenceEquals(visualizedRunSource, sequence) ? lessonPitch : null;
            DrawLessonNoteMap(sequence, currentRun);
            if (currentRun?.Running == true) target = currentRun.Expected;
        }
        else
        {
            ImGui.TextWrapped(SelectedCatalogExercise?.Mode == "guided"
                ? "Guided card: use the PDF and lesson steps for the notes. Only live input is shown here."
                : "No local note sequence loaded for this exercise. Use Pitch targets and setup to load one.");
        }
        if (ImGui.TreeNode("Live input graph##lessons"))
        {
            DrawInputVisualizer(target);
            ImGui.TreePop();
        }
    }

    private void DrawLessonNoteMap(LessonSequence sequence, LessonPitchPractice? run)
    {
        var columnWidth = ImGui.CalcTextSize("Pending   ").X;
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / columnWidth), 1, Math.Min(8, sequence.Notes.Length));
        var lastPage = LessonVisualizerPage.PageAt(sequence.Notes.Length - 1, sequence.Notes.Length, columns);
        if (run?.Running == true) lessonVisualPage = LessonVisualizerPage.PageAt(run.Index, sequence.Notes.Length, columns);
        lessonVisualPage = Math.Clamp(lessonVisualPage, 0, lastPage);
        ImGui.TextWrapped("Note order, not time. Each tile is one target, including repeated pitches.");
        ImGui.BeginDisabled(run?.Running == true || lessonVisualPage == 0);
        if (ImGui.Button("Previous targets")) lessonVisualPage--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(run?.Running == true || lessonVisualPage >= lastPage);
        if (ImGui.Button("Next targets")) lessonVisualPage++;
        ImGui.EndDisabled();
        var first = lessonVisualPage * columns;
        var end = Math.Min(first + columns, sequence.Notes.Length);
        ImGui.TextUnformatted($"Targets {first + 1}–{end} of {sequence.Notes.Length}");
        if (!ImGui.BeginTable("lesson_note_map", columns, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            return;
        ImGui.TableNextRow();
        for (var i = first; i < end; i++)
        {
            ImGui.TableNextColumn();
            var note = sequence.Notes[i];
            var status = run?.Results[i] ?? "preview";
            var active = run?.Running == true && run.Index == i;
            var retries = run?.Retries[i] ?? 0;
            Vector4? ink = active ? new Vector4(0.54f, 0.83f, 1, 1) : status switch
            {
                "matched" => new Vector4(0.39f, 0.97f, 0.66f, 1),
                "skipped" => new Vector4(1, 0.79f, 0.38f, 1),
                _ => null,
            };
            if (active)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.26f, 0.39f, 1)));
            var title = $"{i + 1}. {note.Name}";
            if (ink is { } color) ImGui.TextColored(color, title);
            else ImGui.TextUnformatted(title);
            ImGui.TextUnformatted(note.String is { } s ? $"S{s} F{note.Fret}" : "Pitch");
            ImGui.TextUnformatted(active ? run!.WaitingForMute ? "Mute" : "Ready" : status switch
            {
                "matched" => "Matched",
                "skipped" => "Skipped",
                "waiting" => "Pending",
                _ => "Preview",
            });
            if (retries > 0) ImGui.TextUnformatted($"Wrong: {retries}");
        }
        ImGui.EndTable();
    }
}
