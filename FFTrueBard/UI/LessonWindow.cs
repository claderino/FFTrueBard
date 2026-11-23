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
        catalogPath = config.LessonCatalogFile ?? "";
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
        ResetLessonGuide();
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
            if (queuedLessonCue is { } cue && lessonPlayer.Ready)
            {
                try
                {
                    var (start, end) = cue.Within(lessonPlayer.Duration);
                    if (!lessonPlayer.SetLoop(start, end, false))
                        throw new InvalidDataException("The cue cannot be represented as an audio range.");
                    lessonPlayer.Seek(start);
                    loopA = (float)start;
                    loopB = (float)end;
                    catalogStatus = "Exercise cue loaded without playback. Press Play; enable/apply A-B looping if wanted.";
                }
                catch (Exception e) { catalogStatus = $"Cue not applied: {e.Message}"; }
            }
            queuedLessonCue = null;
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
            tablatureRequest = null;
            LoadLessonCatalog();
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
        ImGui.BeginDisabled(scanningLessons is not null || loadingCatalog is not null || loadingSequence is not null);
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
            StopTools();
            ScanLessons(true);
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(lessonStatus);
        if (lessonLibrary is null) return;
        if (lessonLibrary.Tablature is { } tablature)
            ImGui.TextUnformatted($"Book exercise excerpts: {tablature.Count}/365");
        foreach (var warning in lessonLibrary.Warnings) ImGui.TextWrapped(warning);
        ImGui.TextWrapped($"Resolved PDF: {lessonLibrary.Paths.Pdf}");
        ImGui.TextWrapped($"Exercises: {lessonLibrary.Paths.Exercises}\nRhythm: {lessonLibrary.Paths.Rhythm}");
        if (ImGui.Button("Open local lesson PDF (external viewer)")) OpenLessonPdf();
    }

    private void OpenLessonPdf()
    {
        try
        {
            var path = LessonLibrary.Normalize(lessonLibrary?.Paths.Pdf ?? config.LessonPdf);
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Select an existing local PDF first.");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception e) { lessonStatus = $"Could not open PDF: {e.Message}"; }
    }

    private void LoadLessonTrack(LessonTrack track, LessonAudioCue? cue = null)
    {
        if (lessonPlayer.Loading) return;
        StopTools();
        config.PracticeMode = true;
        trainingPaused = true;
        lessonPlayer.Load(track.Path);
        queuedLessonCue = cue;
        initialiseLoop = true;
        save();
    }

    private void DrawLessonProgress()
    {
        ImGui.Checkbox("Mark exercise complete", ref lessonCompleted);
        ImGui.TextUnformatted("Personal notes");
        ImGui.InputTextMultiline("##personal_notes", ref lessonNotes, 2000, new Vector2(-1, 95));
        if (ImGui.Button("Save progress / notes")) lessonProgress.Save(config.LessonExercise, lessonCompleted, lessonNotes);
        ImGui.SameLine();
        if (ImGui.Button("Discard unsaved lesson edits")) ReadLessonEntry();
        if (LessonDirty) ImGui.TextWrapped("Unsaved edits: save or discard before changing the exercise. Closing/reloading does not save these edits.");
        ImGui.TextWrapped($"{lessonProgress.Completed}/365 self-reported completions. {lessonProgress.Status}");
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
            ImGui.TextWrapped("Weekly audio includes several exercises. Use a local card's cue if available; otherwise seek and set A/B manually.");
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
        if (ImGui.TreeNode("A-B loop"))
        {
            ImGui.InputFloat("Loop A (seconds)", ref loopA, 1, 10, "%.2f");
            ImGui.SameLine();
            if (ImGui.Button("A = current")) loopA = (float)lessonPlayer.Position;
            ImGui.InputFloat("Loop B (seconds)", ref loopB, 1, 10, "%.2f");
            ImGui.SameLine();
            if (ImGui.Button("B = current")) loopB = (float)lessonPlayer.Position;
            ImGui.Checkbox("Enable A-B loop", ref lessonLoop);
            if (ImGui.Button("Apply loop boundaries"))
                lessonStatus = lessonPlayer.SetLoop(loopA, loopB, lessonLoop) ? "Loop settings applied." : "Loop must be within the track, with B at least 0.1 seconds after A.";
            ImGui.TextUnformatted(lessonPlayer.LoopEnabled ? $"Active loop: {lessonPlayer.LoopA:0.00}–{lessonPlayer.LoopB:0.00} s" : "Loop off. Apply after editing.");
            if (lessonStatus.StartsWith("Loop", StringComparison.Ordinal)) ImGui.TextWrapped(lessonStatus);
            ImGui.TreePop();
        }
        ImGui.EndDisabled();
        var volume = lessonPlayer.Volume;
        if (ImGui.SliderFloat("Lesson playback volume", ref volume, 0, 1, "%.2f"))
        {
            lessonPlayer.Volume = config.LessonVolume = volume;
            lessonPlayer.ApplyVolume();
            save();
        }
        ImGui.TextDisabled("Use headphones. Playback uses the Windows default output.");
    }

    private void DrawLessonAudio()
    {
        if (SelectedCatalogExercise is { Audio: { } cue } lesson)
        {
            ImGui.BeginDisabled(lessonPlayer.Loading || loadingCatalog is not null || lessonLibrary is null);
            if (ImGui.Button("Load exercise cue"))
            {
                try { LoadLessonTrack(cue.ResolveTrack(lessonLibrary!, lesson.Exercise), cue); }
                catch (Exception e) { catalogStatus = $"Cannot load exercise cue: {e.Message}"; }
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped($"Cue: {cue.StartSeconds:0.##} s to {cue.EndSeconds?.ToString("0.##") ?? "track end"}. " +
                "No autoplay. Enable A-B looping to repeat this range.");
        }
        if (catalogStatus.StartsWith("Cannot load exercise cue", StringComparison.Ordinal) ||
            catalogStatus.StartsWith("Cue not applied", StringComparison.Ordinal))
            ImGui.TextWrapped(catalogStatus);
        if (lessonPlayer.Loading || lessonPlayer.Ready || lessonPlayer.Path.Length > 0) DrawLessonPlayback();
        if (lessonLibrary is null)
        {
            ImGui.TextWrapped("Set your local folders to browse lesson audio.");
            if (ImGui.Button("Open lesson files")) requestedTab = "Lesson files";
        }
        else if (ImGui.CollapsingHeader("Browse exercise and backing tracks", lessonPlayer.Ready ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
            DrawLessonTracks();
    }

    private void DrawLessonFiles()
    {
        DrawLessonLocations();
        DrawCatalogSetup();
    }

    private void DrawLessons()
    {
        if (!lessonScanRequested) ScanLessons(false);
        DrawExerciseSelector();
        ImGui.Spacing();
        if (!ImGui.BeginTabBar("lesson_tools")) return;
        DrawToolTab("Guide", DrawLessonGuide);
        DrawToolTab("Tablature", DrawLessonTablature);
        DrawToolTab("Pitch drill", DrawLessonPitch);
        DrawToolTab("Audio", DrawLessonAudio);
        DrawToolTab("Notes", DrawLessonProgress);
        ImGui.EndTabBar();
    }
}
