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
    private int sequenceRequest;
    private int loadingSequenceRequest;
    private bool reloadPitchSequence;

    private void LoadPitchSequence()
    {
        lessonPitch.Stop("Pitch sequence changed. Review it, then restart grading.");
        pitchSequence = null;
        sequenceReviewed = false;
        visualizedRunSource = null;
        lessonVisualPage = 0;
        sequenceRequest++;
        if (loadingSequence is not null) { reloadPitchSequence = true; return; }
        if (SelectedCatalogExercise?.Mode == "guided")
        {
            sequenceStatus = "This card is guided/self-assessed. Pitch grading is not enabled for its technique.";
            return;
        }
        try
        {
            var root = lessonLibrary?.Paths.Root ?? config.LessonRoot;
            var pdf = lessonLibrary?.Paths.Pdf ?? config.LessonPdf;
            var path = string.IsNullOrWhiteSpace(sequencePath)
                ? SelectedCatalogExercise?.Mode == "pitch-order"
                    ? SelectedCatalogExercise.SequencePath(loadedCatalogPath)
                    : LessonSequence.DefaultPath(root, pdf, config.LessonExercise)
                : LessonLibrary.Normalize(sequencePath);
            loadingSequenceExercise = config.LessonExercise;
            if (!File.Exists(path))
            {
                sequenceStatus = path.Length == 0 ? "Set lesson locations first, or browse to a local .notes.json file." :
                    $"No pitch data at {path}. Browse to your local .notes.json file. No course sequence is bundled.";
                return;
            }
            var exercise = loadingSequenceExercise;
            loadingSequenceRequest = sequenceRequest;
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
                if (loadingSequenceExercise == config.LessonExercise && loadingSequenceRequest == sequenceRequest)
                {
                    pitchSequence = loaded;
                    sequenceStatus = $"Loaded {loaded.Notes.Length} pitch targets for exercise {loaded.Exercise}. Review before starting.";
                }
            }
            catch (Exception e) { sequenceStatus = $"Cannot load pitch data: {e.Message}"; }
            loadingSequence = null;
            if (reloadPitchSequence) { reloadPitchSequence = false; LoadPitchSequence(); }
        }
        CheckLessonCapture();
        lessonPitch.Tick(Now);
    }

    private void CheckLessonCapture()
    {
        if (!lessonPitch.Running) return;
        if (!detector.IsCapturing || pitchAudioDrops != detector.DroppedAudioBlocks || pitchFrameDrops != detector.DroppedDetectionFrames)
            lessonPitch.Stop("Capture stopped or dropped data. No mistakes added. Check Input, then restart.");
    }

    private void ReceiveLessonPitch(DetectionFrame frame)
    {
        CheckLessonCapture();
        lessonPitch.Receive(frame, Now);
    }

    private void DrawLessonPitch()
    {
        if (SelectedCatalogExercise?.Mode == "guided")
        {
            ImGui.TextWrapped("This exercise is self-assessed. The live graph is available without grading.");
            if (ImGui.Button("Open guide")) requestedTab = "Guide";
            if (ImGui.TreeNode("Live input graph"))
            {
                DrawInputVisualizer();
                ImGui.TreePop();
            }
            return;
        }
        ImGui.TextWrapped("Pick one note, then mute until Ready. Pitch order only; no rhythm or technique score.");
        var sequence = pitchSequence is { } loaded && loaded.Exercise == config.LessonExercise ? loaded : null;
        var currentRun = sequence is not null && ReferenceEquals(visualizedRunSource, sequence);
        if (lessonPitch.Running)
        {
            if (ImGui.Button("Stop drill")) lessonPitch.Stop();
            ImGui.SameLine();
            if (ImGui.Button("Skip target")) lessonPitch.Skip();
        }
        else if (sequence is not null)
        {
            ImGui.Checkbox("I reviewed these targets against my lesson", ref sequenceReviewed);
            ImGui.BeginDisabled(!sequenceReviewed || !detector.IsCapturing ||
                loadingSequence is not null || loadingCatalog is not null || scanningLessons is not null);
            if (PrimaryButton(currentRun ? "Restart drill" : "Start drill"))
            {
                EnterTool(); // Stops lesson playback and other training tools.
                pitchAudioDrops = detector.DroppedAudioBlocks;
                pitchFrameDrops = detector.DroppedDetectionFrames;
                lessonPitch.Start(sequence, Now, config.LessonPitchTolerance);
                visualizedRunSource = sequence;
                currentRun = true;
                lessonVisualPage = 0;
            }
            ImGui.EndDisabled();
            if (!detector.IsCapturing)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Open input setup")) requestedTab = "Setup";
            }
        }
        if (currentRun) DrawLessonPitchFeedback();
        DrawLessonVisualizer();
        if (currentRun) DrawLessonScoreDetails();

        if (!ImGui.CollapsingHeader("Pitch targets and setup", sequence is null ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            return;
        ImGui.BeginDisabled(lessonPitch.Running || loadingSequence is not null || loadingCatalog is not null || scanningLessons is not null);
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
        ImGui.TextWrapped("Leave blank to use the selected catalog card's note file, or discover fftruebard-lessons/exercise-NNN.notes.json under the saved root when no card exists. Files stay local.");
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
        if (sequence is not null)
        {
            ImGui.TextWrapped($"{sequence.Title}\nSource: {sequence.Source}");
            if (ImGui.TreeNode("Review expected sequence"))
            {
                ImGui.BeginChild("review_pitch_sequence", new Vector2(0, 120));
                for (var i = 0; i < sequence.Notes.Length; i++)
                {
                    var note = sequence.Notes[i];
                    ImGui.TextUnformatted($"{i + 1}: {note.Name} | {note.Hint}");
                }
                ImGui.EndChild();
                ImGui.TreePop();
            }
            ImGui.BeginDisabled(lessonPitch.Running);
            var tolerance = float.IsFinite(config.LessonPitchTolerance) ? Math.Clamp(config.LessonPitchTolerance, 5, 50) : 35;
            if (ImGui.SliderFloat("Lesson pitch tolerance (cents)", ref tolerance, 5, 50, "%.0f"))
            {
                config.LessonPitchTolerance = tolerance;
                save();
            }
            ImGui.EndDisabled();
        }
    }

    private void DrawLessonPitchFeedback()
    {
        var color = lessonPitch.State switch
        {
            "correct" => new Vector4(0.3f, 1, 0.45f, 1),
            "wrong" => new Vector4(1, 0.35f, 0.35f, 1),
            "uncertain" => new Vector4(1, 0.8f, 0.25f, 1),
            _ => new Vector4(0.65f, 0.85f, 1, 1),
        };
        if (lessonPitch.Running && lessonPitch.Expected is { } expected)
        {
            ImGui.TextColored(lessonPitch.WaitingForMute ? color : new Vector4(0.53f, 0.94f, 0.76f, 1),
                lessonPitch.WaitingForMute ? $"Mute to rearm  |  Next: {expected.Name}" : $"Ready  |  Pick {expected.Name}");
            if (lessonPitch.WaitingForMute && lessonPitch.State is ("wrong" or "uncertain"))
                ImGui.TextWrapped(lessonPitch.Feedback);
        }
        else ImGui.TextWrapped(lessonPitch.Feedback);

        if (lessonPitch.Sequence is { } run)
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.15f, 0.43f, 0.39f, 1));
            ImGui.ProgressBar((float)lessonPitch.Index / run.Notes.Length, new Vector2(-1, 0), $"{lessonPitch.Index}/{run.Notes.Length} targets");
            ImGui.PopStyleColor();
            ImGui.TextWrapped($"{lessonPitch.Correct} matched  /  {lessonPitch.Wrong} wrong attempts  /  {lessonPitch.Skipped} skipped");
        }
    }

    private void DrawLessonScoreDetails()
    {
        if (!ImGui.TreeNode("Score details")) return;
        var attempts = lessonPitch.Correct + lessonPitch.Wrong;
        ImGui.TextWrapped(attempts == 0 ? "No scored pitch attempts yet." :
            $"Pitch-attempt accuracy: {100.0 * lessonPitch.Correct / attempts:0.0}% (skips excluded).");
        ImGui.TextWrapped($"{lessonPitch.UncertainFrames} unscored frames. Silence is not a mistake; results aren't saved.");
        ImGui.TreePop();
    }
}
