using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Detection;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private readonly LessonPitchPractice lessonPitch = new();
    private LessonSequence? pitchSequence;
    private Task<LessonSequence>? loadingSequence;
    private string sequencePath = "";
    private string sequenceStatus = "";
    private bool sequenceReviewed;
    private int loadingSequenceExercise;
    private long pitchAudioDrops;
    private long pitchFrameDrops;

    private void LoadPitchSequence()
    {
        if (loadingSequence is not null) return;
        lessonPitch.Stop("Pitch sequence changed. Review it, then restart grading.");
        pitchSequence = null;
        sequenceReviewed = false;
        try
        {
            var root = lessonLibrary?.Paths.Root ?? config.LessonRoot;
            var pdf = lessonLibrary?.Paths.Pdf ?? config.LessonPdf;
            var path = string.IsNullOrWhiteSpace(sequencePath)
                ? LessonSequence.DefaultPath(root, pdf, config.LessonExercise) : LessonLibrary.Normalize(sequencePath);
            loadingSequenceExercise = config.LessonExercise;
            if (!File.Exists(path))
            {
                sequenceStatus = path.Length == 0 ? "Set lesson locations first, or browse to a local .notes.json file." :
                    $"No pitch data at {path}. Browse to your local .notes.json file. No course sequence is bundled.";
                return;
            }
            var exercise = loadingSequenceExercise;
            loadingSequence = Task.Run(() => LessonSequence.Load(path, exercise));
            sequenceStatus = "Loading local pitch sequence...";
        }
        catch (Exception e) { sequenceStatus = $"Cannot load pitch data: {e.Message}"; }
    }

    private void UpdateLessonPitch()
    {
        if (loadingSequence?.IsCompleted == true)
        {
            try
            {
                var loaded = loadingSequence.GetAwaiter().GetResult();
                if (loadingSequenceExercise == config.LessonExercise)
                {
                    pitchSequence = loaded;
                    sequenceStatus = $"Loaded {loaded.Notes.Length} pitch targets for exercise {loaded.Exercise}. Review before starting.";
                }
            }
            catch (Exception e) { sequenceStatus = $"Cannot load pitch data: {e.Message}"; }
            loadingSequence = null;
        }
        CheckLessonCapture();
        lessonPitch.Tick(Now);
    }

    private void CheckLessonCapture()
    {
        if (!lessonPitch.Running) return;
        if (!detector.IsCapturing || pitchAudioDrops != detector.DroppedAudioBlocks || pitchFrameDrops != detector.DroppedDetectionFrames)
            lessonPitch.Stop("Capture stopped or data was dropped — grading stopped without adding mistakes. Check Audio & tuning, then restart this drill.");
    }

    private void ReceiveLessonPitch(DetectionFrame frame)
    {
        CheckLessonCapture();
        lessonPitch.Receive(frame, Now);
    }

    private void DrawLessonPitch()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Visual pitch-order drill (slow, mute-separated pilot)");
        ImGui.TextWrapped("Pick ONE note, mute all strings until Ready, then pick again. Repeated pitches require a mute. This does not follow the MP3 clock or grade sixteenth-note speed, picking direction, rhythm, or fingering.");
        DrawLessonPitchFeedback();
        if (!ImGui.CollapsingHeader("Pitch targets and setup", pitchSequence is null ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            return;
        ImGui.BeginDisabled(lessonPitch.Running || loadingSequence is not null);
        if (ImGui.InputText("Pitch-sequence file override", ref sequencePath, 2048))
        {
            pitchSequence = null;
            sequenceReviewed = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse##pitch_sequence"))
            lessonDialogs.OpenFileDialog("Select local pitch sequence", ".json", (ok, path) =>
            {
                if (!ok) return;
                sequencePath = path;
                LoadPitchSequence();
            });
        ImGui.TextWrapped("Leave blank to discover fftruebard-lessons/exercise-NNN.notes.json under the saved lesson root (or beside the PDF when no root is set). Files stay local.");
        if (ImGui.Button("Save sequence location and load"))
        {
            try
            {
                var path = LessonLibrary.Normalize(sequencePath);
                var previous = config.LessonSequenceFile;
                config.LessonSequenceFile = path;
                try { save(); }
                catch { config.LessonSequenceFile = previous; throw; }
                LoadPitchSequence();
            }
            catch (Exception e) { sequenceStatus = $"Cannot save pitch-data location: {e.Message}"; }
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(sequenceStatus);
        if (pitchSequence is not null && pitchSequence.Exercise == config.LessonExercise)
        {
            ImGui.TextWrapped($"{pitchSequence.Title}\nSource supplied by local file: {pitchSequence.Source}");
            ImGui.TextDisabled("String/fret annotations are suggestions only; the detector cannot identify the physical string.");
            if (ImGui.TreeNode("Review expected sequence"))
            {
                ImGui.BeginChild("review_pitch_sequence", new Vector2(0, 120));
                for (var i = 0; i < pitchSequence.Notes.Length; i++)
                {
                    var note = pitchSequence.Notes[i];
                    ImGui.TextUnformatted($"{i + 1}: {note.Name} | {note.Hint}");
                }
                ImGui.EndChild();
                ImGui.TreePop();
            }
            ImGui.BeginDisabled(lessonPitch.Running);
            ImGui.Checkbox("I reviewed these targets against my lesson", ref sequenceReviewed);
            var tolerance = float.IsFinite(config.LessonPitchTolerance) ? Math.Clamp(config.LessonPitchTolerance, 5, 50) : 35;
            if (ImGui.SliderFloat("Lesson pitch tolerance (cents)", ref tolerance, 5, 50, "%.0f"))
            {
                config.LessonPitchTolerance = tolerance;
                save();
            }
            ImGui.EndDisabled();
            ImGui.BeginDisabled(!sequenceReviewed || !detector.IsCapturing || loadingSequence is not null);
            if (ImGui.Button("Start / restart visual pitch drill"))
            {
                EnterTool(); // Stops lesson audio, other tools, and the preceding run.
                pitchAudioDrops = detector.DroppedAudioBlocks;
                pitchFrameDrops = detector.DroppedDetectionFrames;
                lessonPitch.Start(pitchSequence, Now, config.LessonPitchTolerance);
            }
            ImGui.EndDisabled();
        }
    }

    private void DrawLessonPitchFeedback()
    {
        ImGui.BeginDisabled(!lessonPitch.Running);
        if (ImGui.Button("Stop visual pitch drill")) lessonPitch.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Skip expected note (explicitly unplayed)")) lessonPitch.Skip();
        ImGui.EndDisabled();

        var color = lessonPitch.State switch
        {
            "correct" => new Vector4(0.3f, 1, 0.45f, 1),
            "wrong" => new Vector4(1, 0.35f, 0.35f, 1),
            "uncertain" => new Vector4(1, 0.8f, 0.25f, 1),
            _ => new Vector4(0.65f, 0.85f, 1, 1),
        };
        ImGui.TextColored(color, $"{lessonPitch.State.ToUpperInvariant()} | {lessonPitch.Outcome}");
        ImGui.TextWrapped(lessonPitch.Feedback);
        if (lessonPitch.Running && lessonPitch.Expected is { } expected)
        {
            ImGui.TextUnformatted($"NEXT #{lessonPitch.Index + 1}: {expected.Name} | {expected.Hint}");
            ImGui.TextUnformatted(lessonPitch.WaitingForMute ? "MUTE to rearm" : "READY for next pick");
        }
        if (lessonPitch.Sequence is { } run)
        {
            ImGui.TextUnformatted($"Run for exercise {run.Exercise} (results retained until restart)");
            ImGui.ProgressBar((float)lessonPitch.Index / run.Notes.Length, new Vector2(-1, 20), $"{lessonPitch.Index}/{run.Notes.Length} targets resolved");
            var attempts = lessonPitch.Correct + lessonPitch.Wrong;
            ImGui.TextWrapped($"Matched: {lessonPitch.Correct} | Wrong-pitch attempts: {lessonPitch.Wrong} | Explicit skips: {lessonPitch.Skipped} | Unresolved: {run.Notes.Length - lessonPitch.Index}");
            ImGui.TextWrapped(attempts == 0 ? "No scored pitch attempts yet." : $"Pitch-attempt accuracy: {100.0 * lessonPitch.Correct / attempts:0.0}% (retries included; skips excluded).");
            ImGui.TextWrapped($"Unscored uncertain frames: {lessonPitch.UncertainFrames} (frames, not missed picks). No automatic missing-note or timing score.");
            var first = Math.Max(0, lessonPitch.Index - 2);
            for (var i = first; i < Math.Min(run.Notes.Length, first + 8); i++)
                ImGui.TextUnformatted($"{(i == lessonPitch.Index ? ">" : " ")} #{i + 1} {run.Notes[i].Name}: {lessonPitch.Results[i]} | wrong attempts {lessonPitch.Retries[i]}");
        }
        if (latest is null || Now - latest.TimestampMs > 500)
            ImGui.TextUnformatted("Live input: no recent pitch frame. Check capture.");
        else
            DrawLivePitch();
        ImGui.TextWrapped("Green = matched event; red = wrong pitch; amber = uncertain / interrupted. Labels accompany colors. Silence is not a mistake. Runs stay on screen only; lesson completion remains self-reported. Starting audio or another tool stops this drill.");
    }
}
