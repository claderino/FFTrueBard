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
    private int exerciseJump;
    private bool[] lessonSelfChecks = [];
    private (LessonAudioCue Cue, bool PlayWhenReady)? queuedLessonCue;
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
            catalogStatus = $"Loaded {lessonCatalog.Exercises.Length} local guided lesson cards.";
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
        if (!ImGui.TreeNode("Exercise catalog")) return;
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
        ImGui.TextWrapped(catalogStatus);
        if (lessonCatalog is { } catalog)
            ImGui.TextWrapped($"{catalog.Title}\nSource: {catalog.Source}");
        ImGui.TreePop();
    }

    private void DrawExerciseSelector()
    {
        var exercise = config.LessonExercise;
        var lesson = SelectedCatalogExercise;
        var title = lesson?.Title ?? (lessonLibrary?.Tablature?.Find(exercise) is not null ? "Book exercise" : "Exercise library");
        var currentWeek = Math.Min(52, (exercise - 1) / 7 + 1);
        var width = ImGui.GetContentRegionAvail().X;
        var left = ImGui.GetCursorPosX();
        var scale = ReaderScale;
        var jumpWidth = ImGui.CalcTextSize("Jump to exercise").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();
        var navigationWidth = jumpWidth + 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        ImGui.BeginGroup();
        ImGui.TextColored(ReaderGold, $"WEEK {currentWeek:00}  /  EXERCISE {exercise:000} OF 365");
        // Use the body font for titles with glyphs outside the optional display font.
        float titleWidth;
        using (title.All(c => c <= 0xff) ? lessonHeading?.Push() : null)
        {
            titleWidth = ImGui.CalcTextSize(title).X;
            ImGui.TextWrapped(title);
        }
        ImGui.EndGroup();
        if (width >= titleWidth + navigationWidth + 28 * scale && width >= 395 * scale)
            ImGui.SameLine(left + width - navigationWidth);
        ImGui.BeginGroup();
        ImGui.BeginDisabled(LessonNavigationBlocked);
        ImGui.BeginDisabled(exercise == 1);
        if (ImGui.ArrowButton("previous_exercise", ImGuiDir.Left)) SelectLessonExercise(exercise - 1);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous exercise");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(1, Math.Min(jumpWidth, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X)));
        if (ImGui.BeginCombo("##exercise_picker", "Jump to exercise"))
        {
            if (ImGui.IsWindowAppearing()) exerciseJump = exercise;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
            ImGui.InputInt("##exercise_jump", ref exerciseJump, 0);
            ImGui.SameLine();
            ImGui.BeginDisabled(exerciseJump is < 1 or > 365);
            if (ImGui.Button("Go to exercise"))
            {
                SelectLessonExercise(exerciseJump);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            if (ImGui.BeginCombo("Jump to week", $"Week {currentWeek}"))
            {
                for (var week = 1; week <= 52; week++)
                    if (ImGui.Selectable($"Week {week}")) SelectLessonExercise((week - 1) * 7 + 1);
                ImGui.EndCombo();
            }
            if (lessonCatalog is not null)
            {
                if (ImGui.TreeNode("Filter local cards"))
                {
                    if (ImGui.BeginCombo("Week", catalogWeek == 0 ? "All weeks" : $"Week {catalogWeek}"))
                    {
                        for (var week = 0; week <= 52; week++)
                            if (ImGui.Selectable(week == 0 ? "All weeks" : $"Week {week}", catalogWeek == week))
                                catalogWeek = week;
                        ImGui.EndCombo();
                    }
                    if (ImGui.BeginCombo("Technique", catalogTechnique.Length == 0 ? "All techniques" : catalogTechnique))
                    {
                        foreach (var technique in new[] { "" }.Concat(lessonCatalog.Exercises.Select(e => e.Technique).Distinct().Order()))
                            if (ImGui.Selectable(technique.Length == 0 ? "All techniques" : technique, catalogTechnique == technique))
                                catalogTechnique = technique;
                        ImGui.EndCombo();
                    }
                    ImGui.TreePop();
                }
                var cards = lessonCatalog.Filter(catalogWeek, catalogTechnique).ToArray();
                if (cards.Length == 0) ImGui.TextUnformatted("No authored cards match these filters.");
                foreach (var card in cards)
                    if (ImGui.Selectable($"#{card.Exercise} — {card.Title}##catalog_{card.Exercise}", exercise == card.Exercise))
                        SelectLessonExercise(card.Exercise);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(exercise == 365);
        if (ImGui.ArrowButton("next_exercise", ImGuiDir.Right)) SelectLessonExercise(exercise + 1);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next exercise");
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.EndGroup();
        if (LessonDirty)
        {
            if (ImGui.SmallButton("Unsaved notes")) requestedTab = "Notes";
        }
    }

    private void DrawLessonGuide()
    {
        var lesson = SelectedCatalogExercise;
        if (lesson is null)
        {
            ImGui.TextWrapped("No guided steps for this exercise. Use the book and weekly audio to practise at your own pace.");
            if (lessonLibrary?.Tablature?.Find(config.LessonExercise) is not null)
            {
                if (PrimaryButton("View book exercise")) requestedTab = "Tablature";
                ImGui.SameLine();
                if (ImGui.Button("Open weekly audio")) requestedTab = "Audio";
                return;
            }
            if (ImGui.Button("Open lesson book")) OpenLessonPdf();
            ImGui.SameLine();
            if (ImGui.Button("Lesson files")) requestedTab = "Lesson files";
            if (lessonStatus.Length > 0) ImGui.TextWrapped(lessonStatus);
            if (catalogStatus.Length > 0) ImGui.TextWrapped(catalogStatus);
            return;
        }
        ImGui.TextColored(new Vector4(0.62f, 0.85f, 0.80f, 1), lesson.Technique);
        ImGui.SameLine();
        ImGui.TextDisabled(lesson.Mode == "guided" ? "Self-assessed" : "Optional pitch drill");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.62f, 0.85f, 0.80f, 1), $"Step {lessonStep + 1} of {lesson.Steps.Length}");
        ImGui.TextWrapped(lesson.Steps[lessonStep]);
        ImGui.BeginDisabled(lessonStep == 0);
        if (ImGui.Button("Previous step")) lessonStep--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(lessonStep + 1 >= lesson.Steps.Length);
        if (PrimaryButton("Next step")) lessonStep++;
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("View tablature")) requestedTab = "Tablature";
        ImGui.SameLine();
        if (ImGui.Button("Prepare guided practice")) EnterTool();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pause chord checks and practice this guide without grading.");
        if (lesson.SelfChecks.Length > 0 && ImGui.CollapsingHeader($"Self-checks ({lessonSelfChecks.Count(n => n)}/{lessonSelfChecks.Length})###lesson_checks"))
        {
            for (var i = 0; i < lesson.SelfChecks.Length; i++)
            {
                ImGui.Checkbox($"##lesson_selfcheck_{i}", ref lessonSelfChecks[i]);
                ImGui.SameLine();
                ImGui.TextWrapped(lesson.SelfChecks[i]);
            }
            ImGui.TextDisabled("These checkmarks last for this session.");
        }
        if (ImGui.CollapsingHeader("Book and rhythm references"))
        {
            if (lesson.PdfPage is not null || lesson.PrintedPage.Length > 0)
                ImGui.TextWrapped($"PDF page: {lesson.PdfPage?.ToString() ?? "(unspecified)"} | Printed page: {lesson.PrintedPage}");
            if (lesson.MinBpm is not null) ImGui.TextUnformatted($"Book tempo: {lesson.MinBpm}–{lesson.MaxBpm} BPM");
            if (lesson.RhythmGuide.Length > 0) ImGui.TextWrapped(lesson.RhythmGuide);
        }
    }
}
