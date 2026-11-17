using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private LessonCatalog? lessonCatalog;
    private Task<LessonCatalog>? loadingCatalog;
    private string catalogPath = "";
    private string loadedCatalogPath = "";
    private string catalogStatus = "";
    private int catalogWeek;
    private string catalogTechnique = "";
    private int lessonStep;
    private bool[] lessonSelfChecks = [];
    private LessonAudioCue? queuedLessonCue;
    private LessonExercise? SelectedCatalogExercise => lessonCatalog?.Exercises.FirstOrDefault(e => e.Exercise == config.LessonExercise);
    private bool LessonNavigationBlocked => LessonDirty || lessonPitch.Running || loadingSequence is not null ||
        loadingCatalog is not null || scanningLessons is not null || lessonPlayer.Loading;

    private void LoadLessonCatalog()
    {
        if (loadingCatalog is not null) return;
        StopTools();
        lessonCatalog = null;
        pitchSequence = null;
        sequenceReviewed = false;
        sequenceRequest++;
        ResetLessonGuide();
        try
        {
            var path = string.IsNullOrWhiteSpace(catalogPath)
                ? LessonCatalog.DefaultPath(lessonLibrary?.Paths.Root ?? config.LessonRoot,
                    lessonLibrary?.Paths.Pdf ?? config.LessonPdf)
                : LessonLibrary.Normalize(catalogPath);
            loadedCatalogPath = path;
            if (!File.Exists(path))
            {
                catalogStatus = "No local exercise catalog found. PDF/audio browsing and standalone note files remain available. " +
                    (path.Length > 0 ? $"Expected: {path}" : "Set a lesson root or browse to a .course.json file.");
                LoadPitchSequence();
                return;
            }
            loadingCatalog = Task.Run(() => LessonCatalog.Load(path));
            catalogStatus = "Loading local exercise catalog...";
        }
        catch (Exception e) { catalogStatus = $"Catalog unavailable: {e.Message}"; LoadPitchSequence(); }
    }

    private void UpdateLessonCatalog()
    {
        if (loadingCatalog?.IsCompleted != true) return;
        try
        {
            lessonCatalog = loadingCatalog.GetAwaiter().GetResult();
            catalogStatus = $"Loaded {lessonCatalog.Exercises.Length} local lesson cards. Other exercise numbers remain PDF-only until authored.";
            catalogWeek = 0;
            catalogTechnique = "";
        }
        catch (Exception e) { catalogStatus = $"Catalog rejected; no cards enabled: {e.Message}"; }
        finally
        {
            loadingCatalog = null;
            ResetLessonGuide();
            LoadPitchSequence();
        }
    }

    private void ResetLessonGuide()
    {
        lessonStep = 0;
        lessonSelfChecks = new bool[SelectedCatalogExercise?.SelfChecks.Length ?? 0];
    }

    private void SelectLessonExercise(int exercise)
    {
        if (LessonNavigationBlocked || exercise is < 1 or > 365 || exercise == config.LessonExercise) return;
        var previous = config.LessonExercise;
        try
        {
            StopTools();
            config.LessonExercise = exercise;
            save();
            ReadLessonEntry();
        }
        catch (Exception e)
        {
            config.LessonExercise = previous;
            catalogStatus = $"Cannot select exercise: {e.Message}";
        }
    }

    private void DrawCatalogSetup()
    {
        if (!ImGui.TreeNode("Catalog file and filters")) return;
        ImGui.BeginDisabled(LessonNavigationBlocked);
        if (ImGui.InputText("Local exercise catalog override", ref catalogPath, 2048))
            catalogStatus = "Location edited; save and reload to use it.";
        ImGui.SameLine();
        if (ImGui.Button("Browse##lesson_catalog"))
            lessonDialogs.OpenFileDialog("Select local exercise catalog", ".json", (ok, path) =>
            {
                if (ok) { catalogPath = path; catalogStatus = "Selected; save and reload to use this catalog."; }
            });
        if (ImGui.Button("Save catalog location and reload"))
        {
            try
            {
                var path = LessonLibrary.Normalize(catalogPath);
                var previous = config.LessonCatalogFile;
                config.LessonCatalogFile = path;
                try { save(); }
                catch { config.LessonCatalogFile = previous; throw; }
                LoadLessonCatalog();
            }
            catch (Exception e) { catalogStatus = $"Cannot save catalog location: {e.Message}"; }
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Blank uses fftruebard-lessons/exercises.course.json under the saved lesson root, or beside the PDF. Reload after editing local cards. The plugin does not extract or invent PDF instructions.");
        if (lessonCatalog is not null)
        {
            if (ImGui.BeginCombo("Browse week", catalogWeek == 0 ? "All weeks" : $"Week {catalogWeek}"))
            {
                for (var week = 0; week <= 52; week++)
                    if (ImGui.Selectable(week == 0 ? "All weeks" : $"Week {week}", catalogWeek == week))
                        catalogWeek = week;
                ImGui.EndCombo();
            }
            if (ImGui.BeginCombo("Browse technique", catalogTechnique.Length == 0 ? "All techniques" : catalogTechnique))
            {
                foreach (var technique in new[] { "" }.Concat(lessonCatalog.Exercises.Select(e => e.Technique).Distinct().Order()))
                    if (ImGui.Selectable(technique.Length == 0 ? "All techniques" : technique, catalogTechnique == technique))
                        catalogTechnique = technique;
                ImGui.EndCombo();
            }
        }
        ImGui.TreePop();
    }

    private void DrawLessonCatalog()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Exercise framework — local guided lessons");
        ImGui.TextWrapped(catalogStatus);
        DrawCatalogSetup();
        ImGui.BeginDisabled(LessonNavigationBlocked);
        if (ImGui.Button("Previous exercise")) SelectLessonExercise(config.LessonExercise - 1);
        ImGui.SameLine();
        if (ImGui.Button("Next exercise")) SelectLessonExercise(config.LessonExercise + 1);
        if (lessonCatalog is not null && ImGui.BeginCombo("Catalog exercise", $"#{config.LessonExercise}: {SelectedCatalogExercise?.Title ?? "PDF-only (no card)"}"))
        {
            var cards = lessonCatalog.Filter(catalogWeek, catalogTechnique).ToArray();
            if (cards.Length == 0) ImGui.TextUnformatted("No authored cards match these filters.");
            foreach (var card in cards)
                if (ImGui.Selectable($"#{card.Exercise} — {card.Title}##catalog_{card.Exercise}", config.LessonExercise == card.Exercise))
                    SelectLessonExercise(card.Exercise);
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (LessonDirty) ImGui.TextWrapped("Save or discard personal notes below before changing exercise or catalog.");
        var lesson = SelectedCatalogExercise;
        if (lesson is null)
        {
            ImGui.TextWrapped($"Exercise {config.LessonExercise}: no guided card authored. Use your PDF and weekly audio; this is not an implemented tutorial.");
            return;
        }
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1, 1), $"WEEK {lesson.Week} / EXERCISE {lesson.Exercise}");
        ImGui.TextWrapped($"{lesson.Title} | {lesson.Technique}");
        if (!ImGui.CollapsingHeader("Guided lesson card", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextWrapped($"Catalog: {lessonCatalog!.Title}\nSource supplied by local catalog: {lessonCatalog.Source}");
        ImGui.TextUnformatted(lesson.Mode == "guided" ? "GUIDED / SELF-ASSESSED — automatic grading unavailable for this card." :
            "OPTIONAL PITCH-ORDER PILOT — only reviewed single-note targets; technique and rhythm are self-assessed.");
        if (lesson.PdfPage is not null || lesson.PrintedPage.Length > 0)
            ImGui.TextWrapped($"Book reference: PDF page {lesson.PdfPage?.ToString() ?? "(unspecified)"}; printed page {lesson.PrintedPage}. Open the book and go to this page manually.");
        if (ImGui.Button("Open book for this exercise")) OpenLessonPdf();
        if (lesson.MinBpm is not null)
            ImGui.TextUnformatted($"Book tempo reference: {lesson.MinBpm}–{lesson.MaxBpm} BPM (not an assessed speed).");
        if (lesson.RhythmGuide.Length > 0) ImGui.TextWrapped($"Rhythm reference: {lesson.RhythmGuide}");

        ImGui.BeginDisabled(lessonPlayer.Loading || loadingCatalog is not null || lessonLibrary is null);
        if (lesson.Audio is { } cue)
        {
            ImGui.TextWrapped($"Cue start: {cue.StartSeconds:0.##} s; suggested A/B end: {cue.EndSeconds?.ToString("0.##") ?? "track end"}. Book timestamps can be approximate; listen and adjust. Playback continues past B unless you enable looping or stop it.");
            if (ImGui.Button("Load exercise audio cue (no autoplay)"))
            {
                try { LoadLessonTrack(cue.ResolveTrack(lessonLibrary!, lesson.Exercise), cue); }
                catch (Exception e) { catalogStatus = $"Cannot load exercise cue: {e.Message}"; }
            }
        }
        ImGui.EndDisabled();
        if (ImGui.Button("Prepare guided practice (no grading)")) EnterTool();
        ImGui.TextWrapped($"Step {lessonStep + 1}/{lesson.Steps.Length}: {lesson.Steps[lessonStep]}");
        ImGui.BeginDisabled(lessonStep == 0);
        if (ImGui.Button("Previous step")) lessonStep--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(lessonStep + 1 >= lesson.Steps.Length);
        if (ImGui.Button("Next step")) lessonStep++;
        ImGui.EndDisabled();
        if (lesson.SelfChecks.Length > 0)
        {
            ImGui.TextUnformatted("Self-checks (your observations, not detector results)");
            for (var i = 0; i < lesson.SelfChecks.Length; i++)
            {
                ImGui.Checkbox($"##lesson_selfcheck_{i}", ref lessonSelfChecks[i]);
                ImGui.SameLine();
                ImGui.TextWrapped(lesson.SelfChecks[i]);
            }
        }
        ImGui.TextWrapped("Steps and checkmarks are session-only. Save observations in personal notes below; completion is always your choice. Following a manual card does not require audio capture.");
    }
}
