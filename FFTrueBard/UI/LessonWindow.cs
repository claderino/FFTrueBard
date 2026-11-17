using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private readonly FileDialogManager lessonDialogs = new();
    private readonly LessonPlayer lessonPlayer = new();
    private readonly LessonProgress lessonProgress;
    private LessonLibrary? lessonLibrary;
    private Task<LessonLibrary>? scanningLessons;
    private bool saveLessonPaths;
    private LessonPaths? scannedLessonPaths;
    private bool lessonScanRequested;
    private string lessonRoot = "";
    private string lessonPdf = "";
    private string lessonExercises = "";
    private string lessonRhythm = "";
    private string lessonStatus = "";
    private string lessonNotes = "";
    private bool lessonCompleted;
    private string rhythmStyle = "All styles";
    private int rhythmBpm;
    private float loopA;
    private float loopB;
    private bool lessonLoop;
    private bool initialiseLoop;
    private bool LessonDirty => lessonNotes != lessonProgress.Get(config.LessonExercise).Notes ||
        lessonCompleted != lessonProgress.Get(config.LessonExercise).Completed;

    private void InitializeLessons()
    {
        lessonRoot = config.LessonRoot ?? "";
        lessonPdf = config.LessonPdf ?? "";
        lessonExercises = config.LessonExercisesFolder ?? "";
        lessonRhythm = config.LessonRhythmFolder ?? "";
        sequencePath = config.LessonSequenceFile ?? "";
        config.LessonExercise = Math.Clamp(config.LessonExercise, 1, 365);
        lessonPlayer.Volume = float.IsFinite(config.LessonVolume) ? Math.Clamp(config.LessonVolume, 0, 1) : 0.25f;
        ReadLessonEntry();
    }

    private void ReadLessonEntry()
    {
        lessonPitch.Stop("Exercise changed. Review the selected pitch data and restart.");
        pitchSequence = null;
        sequenceReviewed = false;
        var entry = lessonProgress.Get(config.LessonExercise);
        lessonNotes = entry.Notes;
        lessonCompleted = entry.Completed;
        if (lessonLibrary is not null) LoadPitchSequence();
    }

    private void ScanLessons(bool persist)
    {
        if (scanningLessons is not null) return;
        lessonScanRequested = true;
        saveLessonPaths = persist;
        var paths = new LessonPaths(lessonRoot, lessonPdf, lessonExercises, lessonRhythm);
        scannedLessonPaths = paths;
        scanningLessons = Task.Run(() => LessonLibrary.Scan(paths));
        lessonStatus = "Scanning local lesson locations...";
    }

    private void UpdateLessons()
    {
        lessonPlayer.Update();
        if (initialiseLoop && !lessonPlayer.Loading)
        {
            loopA = 0;
            loopB = (float)lessonPlayer.Duration;
            lessonLoop = false;
            initialiseLoop = false;
        }
        if (scanningLessons?.IsCompleted != true) return;
        try
        {
            var library = scanningLessons.GetAwaiter().GetResult();
            if (saveLessonPaths)
            {
                var old = new LessonPaths(config.LessonRoot, config.LessonPdf, config.LessonExercisesFolder, config.LessonRhythmFolder);
                // Store the user's overrides, not auto-discovered values: selecting another root can rediscover its children.
                config.LessonRoot = LessonLibrary.Normalize(scannedLessonPaths!.Root);
                config.LessonPdf = LessonLibrary.Normalize(scannedLessonPaths.Pdf);
                config.LessonExercisesFolder = LessonLibrary.Normalize(scannedLessonPaths.Exercises);
                config.LessonRhythmFolder = LessonLibrary.Normalize(scannedLessonPaths.Rhythm);
                try { save(); }
                catch
                {
                    config.LessonRoot = old.Root;
                    config.LessonPdf = old.Pdf;
                    config.LessonExercisesFolder = old.Exercises;
                    config.LessonRhythmFolder = old.Rhythm;
                    throw;
                }
            }
            lessonLibrary = library;
            LoadPitchSequence();
            lessonStatus = $"{(saveLessonPaths ? "Saved locations. " : "")}{library.Exercises.Count} exercise/tuning tracks; {library.Rhythm.Count} rhythm tracks. Files remain in place.";
        }
        catch (Exception e) { lessonStatus = $"Could not load lesson locations: {e.Message}"; }
        finally { scanningLessons = null; }
    }

    private void LessonFolderField(string label, ref string value, Action<string> select)
    {
        ImGui.InputText(label, ref value, 2048);
        ImGui.SameLine();
        if (ImGui.Button($"Browse##{label}"))
            lessonDialogs.OpenFolderDialog(label, (ok, path) => { if (ok) select(path); });
    }

    private void DrawLessonLocations()
    {
        ImGui.TextWrapped("Use your own local book and MP3 files. Nothing is downloaded, copied into the plugin, or uploaded. Each PC has its own saved paths.");
        ImGui.BeginDisabled(scanningLessons is not null);
        LessonFolderField("Lesson root folder", ref lessonRoot, p => lessonRoot = p);
        ImGui.TextWrapped("For Guitar Aerobics, select the folder containing the PDF and both audio folders. Blank overrides below use automatic discovery.");
        if (ImGui.TreeNode("Individual locations / overrides"))
        {
            ImGui.InputText("Lesson PDF file", ref lessonPdf, 2048);
            ImGui.SameLine();
            if (ImGui.Button("Browse##lesson_pdf"))
                lessonDialogs.OpenFileDialog("Select lesson PDF", ".pdf", (ok, path) => { if (ok) lessonPdf = path; });
            LessonFolderField("Exercise MP3 folder", ref lessonExercises, p => lessonExercises = p);
            LessonFolderField("Rhythm MP3 folder", ref lessonRhythm, p => lessonRhythm = p);
            ImGui.TextWrapped("Folders are scanned directly, not recursively. MP3 and PDF files may be in separate locations. Absolute paths on this PC only.");
            ImGui.TreePop();
        }
        if (ImGui.Button("Save locations and scan"))
        {
            lessonPlayer.Stop();
            ScanLessons(true);
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(lessonStatus);
        if (lessonLibrary is null) return;
        foreach (var warning in lessonLibrary.Warnings) ImGui.TextWrapped(warning);
        ImGui.TextWrapped($"Resolved PDF: {lessonLibrary.Paths.Pdf}");
        ImGui.TextWrapped($"Exercises: {lessonLibrary.Paths.Exercises}\nRhythm: {lessonLibrary.Paths.Rhythm}");
        if (ImGui.Button("Open local lesson PDF (external viewer)"))
        {
            try
            {
                var path = LessonLibrary.Normalize(lessonLibrary.Paths.Pdf);
                if (!File.Exists(path) || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("Select an existing local PDF first.");
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception e) { lessonStatus = $"Could not open PDF: {e.Message}"; }
        }
    }

    private void LoadLessonTrack(LessonTrack track)
    {
        StopTools();
        config.PracticeMode = true;
        trainingPaused = true;
        lessonPlayer.Load(track.Path);
        initialiseLoop = true;
        save();
    }

    private void DrawLessonProgress()
    {
        var exercise = config.LessonExercise;
        ImGui.BeginDisabled(LessonDirty || lessonPitch.Running || loadingSequence is not null);
        if (ImGui.SliderInt("Exercise / day", ref exercise, 1, 365))
        {
            config.LessonExercise = exercise;
            ReadLessonEntry();
            save();
        }
        if (ImGui.BeginCombo("Week", $"Week {Math.Min(52, (exercise - 1) / 7 + 1)}"))
        {
            for (var week = 1; week <= 52; week++)
                if (ImGui.Selectable($"Week {week}", week == Math.Min(52, (exercise - 1) / 7 + 1)))
                {
                    config.LessonExercise = (week - 1) * 7 + 1;
                    ReadLessonEntry();
                    save();
                }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        ImGui.Checkbox("Completed (self-reported)", ref lessonCompleted);
        ImGui.InputTextMultiline("Personal lesson notes", ref lessonNotes, 2000, new Vector2(-1, 65));
        if (ImGui.Button("Save progress / notes")) lessonProgress.Save(config.LessonExercise, lessonCompleted, lessonNotes);
        ImGui.SameLine();
        if (ImGui.Button("Discard unsaved lesson edits")) ReadLessonEntry();
        if (LessonDirty) ImGui.TextWrapped("Unsaved edits: save or discard before changing the exercise. Closing/reloading does not save these edits.");
        ImGui.TextWrapped($"Completed: {lessonProgress.Completed}/365. {lessonProgress.Status}");
        ImGui.TextWrapped("Completion is self-reported. Optional visual pitch checks below require a reviewed local sequence; rhythm and technique are not graded. The PDF opens in your external viewer.");
    }

    private void DrawLessonTracks()
    {
        if (lessonLibrary is null) return;
        ImGui.BeginDisabled(lessonPlayer.Loading);
        var weekTrack = lessonLibrary.Exercises.FirstOrDefault(t => config.LessonExercise >= t.First && config.LessonExercise <= t.Last);
        if (weekTrack is not null)
        {
            ImGui.TextWrapped($"Exercise {config.LessonExercise}: {weekTrack.Title}");
            if (ImGui.Button("Load selected week's exercise track")) LoadLessonTrack(weekTrack);
            ImGui.TextDisabled("Weekly audio includes several exercises. Seek and set A/B manually; no automatic per-exercise timestamps.");
        }
        if (ImGui.TreeNode("All exercise / tuning tracks"))
        {
            ImGui.BeginChild("lesson_exercise_tracks", new Vector2(0, 130));
            foreach (var track in lessonLibrary.Exercises)
                if (ImGui.Selectable(track.Title + "##" + track.Path, lessonPlayer.Path == track.Path)) LoadLessonTrack(track);
            ImGui.EndChild();
            ImGui.TreePop();
        }
        if (ImGui.TreeNode("Rhythm tracks by style / BPM"))
        {
            if (ImGui.BeginCombo("Style", rhythmStyle))
            {
                foreach (var style in new[] { "All styles" }.Concat(lessonLibrary.Rhythm.Select(t => t.Style).Where(s => s.Length > 0).Distinct().Order()))
                    if (ImGui.Selectable(style, rhythmStyle == style)) rhythmStyle = style;
                ImGui.EndCombo();
            }
            ImGui.InputInt("BPM filter (0 = all)", ref rhythmBpm);
            rhythmBpm = Math.Clamp(rhythmBpm, 0, 999);
            ImGui.BeginChild("lesson_rhythm_tracks", new Vector2(0, 130));
            foreach (var track in lessonLibrary.Rhythm.Where(t => (rhythmStyle == "All styles" || t.Style == rhythmStyle) && (rhythmBpm == 0 || t.Bpm == rhythmBpm)))
                if (ImGui.Selectable(track.Title + "##" + track.Path, lessonPlayer.Path == track.Path)) LoadLessonTrack(track);
            ImGui.EndChild();
            ImGui.TreePop();
        }
        ImGui.EndDisabled();
    }

    private void DrawLessonPlayback()
    {
        ImGui.Separator();
        ImGui.TextWrapped(lessonPlayer.Status);
        if (lessonPlayer.Path.Length > 0) ImGui.TextWrapped($"Track: {Path.GetFileName(lessonPlayer.Path)}");
        ImGui.BeginDisabled(!lessonPlayer.Ready);
        if (ImGui.Button("Play lesson audio"))
        {
            // Playback never contributes to chord grading or mapped inputs.
            mappingCapture.Cancel();
            CancelMeasurements();
            StopMetronome();
            config.PracticeMode = true;
            trainingPaused = true;
            lessonPlayer.Play();
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause audio")) lessonPlayer.Pause();
        ImGui.SameLine();
        if (ImGui.Button("Stop audio")) lessonPlayer.Stop();
        var position = (float)lessonPlayer.Position;
        ImGui.TextUnformatted($"{(lessonPlayer.Playing ? "Playing" : "Paused / stopped")} | {position:0.0} / {lessonPlayer.Duration:0.0} seconds");
        if (ImGui.SliderFloat("Seek (seconds)", ref position, 0, (float)Math.Max(0.1, lessonPlayer.Duration), "%.1f")) lessonPlayer.Seek(position);
        ImGui.InputFloat("Loop A (seconds)", ref loopA, 1, 10, "%.2f");
        ImGui.SameLine();
        if (ImGui.Button("A = current")) loopA = (float)lessonPlayer.Position;
        ImGui.InputFloat("Loop B (seconds)", ref loopB, 1, 10, "%.2f");
        ImGui.SameLine();
        if (ImGui.Button("B = current")) loopB = (float)lessonPlayer.Position;
        ImGui.Checkbox("Enable A-B loop", ref lessonLoop);
        if (ImGui.Button("Apply loop boundaries"))
            lessonStatus = lessonPlayer.SetLoop(loopA, loopB, lessonLoop) ? "Loop settings applied." : "Loop must be within the track, with B at least 0.1 seconds after A.";
        ImGui.TextUnformatted(lessonPlayer.LoopEnabled ? $"Active loop: {lessonPlayer.LoopA:0.00}–{lessonPlayer.LoopB:0.00} s" : "Loop currently off. Apply after editing.");
        ImGui.EndDisabled();
        var volume = lessonPlayer.Volume;
        if (ImGui.SliderFloat("Lesson playback volume", ref volume, 0, 1, "%.2f"))
        {
            lessonPlayer.Volume = config.LessonVolume = volume;
            lessonPlayer.ApplyVolume();
            save();
        }
        ImGui.TextWrapped("Use headphones and the dry guitar input to avoid detecting the backing track. Playback uses Windows default output; changing output devices may require reloading the track. Closing this window or starting another training tool stops playback.");
    }

    private void DrawLessons()
    {
        if (!lessonScanRequested) ScanLessons(false);
        if (ImGui.CollapsingHeader("Local lesson locations", ImGuiTreeNodeFlags.DefaultOpen)) DrawLessonLocations();
        DrawLessonPitch();
        if (ImGui.CollapsingHeader("Lesson selection and self-reported progress", ImGuiTreeNodeFlags.DefaultOpen))
            DrawLessonProgress();
        DrawLessonTracks();
        DrawLessonPlayback();
    }
}
