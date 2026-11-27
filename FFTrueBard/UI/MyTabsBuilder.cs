using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record TabDraftPage(string Pdf, int Page, string Hash, TabDraftStaff[] Staves,
        TabPdfRhythm.Reading[] Rhythms, TabPdfRhythm.Settings Settings, bool StandardNotationOnly)
    {
        public static TabDraftPage From(string pdf, TabPdfScan.Page page) => new(pdf, page.Number, page.Hash,
            page.Detection.Staves, page.Rhythms, page.Settings, page.Detection is { SixLineRows: 0, FiveLineRows: >= 2 });
    }
    private const string DraftPreview = "My Tabs draft preview";
    private TabDraftPage? tabDraft;
    private Task<TabDraftPage>? tabDraftScan;
    private (string Pdf, Task<TabPdfScan.Score> Task)? tabWholeScan;
    private TabPdfScan.Score? tabWholeReading;
    private bool tabWholePreview;
    private (string Pdf, Task<string> Task)? tabDraftSave;
    private List<TabGuideBuilder.Column> tabDraftColumns = [];
    private TabPdfGuide? tabDraftPreviewScore;
    private string tabDraftStatus = "", tabDraftLabel = "", tabDraftTuning = "40 45 50 55 59 64";
    private int tabDraftStaff, tabDraftColumn, tabDraftFirst, tabDraftLast, tabDraftBpm, tabDraftCapo;
    private bool tabDraftReviewed, tabDraftInsert;
    private TabPdfGuide.SaveMode tabDraftSaveMode;

    private void ResetMyTabBuilder()
    {
        tabDraft = null;
        tabDraftPreviewScore = null;
        tabWholeReading = null;
        tabWholePreview = false;
        tabDraftColumns.Clear();
        tabDraftStatus = "";
        tabDraftBpm = tabDraftCapo = 0;
        tabDraftTuning = "40 45 50 55 59 64";
        tabDraftReviewed = tabDraftInsert = false;
        tabDraftSaveMode = TabPdfGuide.SaveMode.Create;
    }

    private void FinishMyTabBuilderJobs(TabPracticeFile tab)
    {
        if (tabDraftScan?.IsCompleted == true)
        {
            try
            {
                var page = tabDraftScan.GetAwaiter().GetResult();
                if (page.Pdf == tab.Pdf) ApplyTabDraftPage(page);
            }
            catch (Exception e) { tabDraftStatus = $"Could not scan TAB notation: {e.Message}"; }
            finally { tabDraftScan = null; }
        }
        if (tabWholeScan is { Task.IsCompleted: true } whole)
        {
            tabWholeScan = null;
            try
            {
                var reading = whole.Task.GetAwaiter().GetResult();
                if (whole.Pdf == tab.Pdf)
                {
                    tabWholeReading = reading;
                    tabDraftStatus = "";
                    if (tabDraft is null && !tabWholePreview && myTabPage <= reading.Pages.Length)
                        ApplyTabDraftPage(TabDraftPage.From(tab.Pdf, reading.Pages[myTabPage - 1]));
                }
            }
            catch (Exception e) { tabDraftStatus = $"Could not scan the score: {e.Message}"; }
        }
        if (tabDraftSave is { Task.IsCompleted: true } saveJob)
        {
            tabDraftSave = null;
            try
            {
                var backup = saveJob.Task.GetAwaiter().GetResult();
                var path = Path.ChangeExtension(saveJob.Pdf, ".tab-guide.json");
                for (var i = 0; i < myTabs.Length; i++)
                    if (myTabs[i].Pdf == saveJob.Pdf)
                        myTabs[i] = myTabs[i] with { Guide = path };
                if (saveJob.Pdf == tab.Pdf)
                {
                    if (myTabToolActive) StopTools();
                    tabWholePreview = false;
                    tabDraftPreviewScore = null;
                    myTab = tab with { Guide = path };
                    ResetMyTabScore();
                    tabDraftStatus = backup.Length == 0 ? "Reviewed guide saved beside the PDF."
                        : $"Reviewed guide saved. Previous guide kept as {Path.GetFileName(backup)}.";
                    tabDraftReviewed = false;
                    tabDraftSaveMode = TabPdfGuide.SaveMode.Create;
                }
            }
            catch (Exception e) { tabDraftStatus = $"Could not save the reviewed guide: {e.Message}"; }
        }
    }

    private void ApplyTabDraftPage(TabDraftPage page)
    {
        tabDraft = page;
        if (page.Staves.Length == 0)
        {
            tabDraftColumns = [];
            tabDraftStatus = page.StandardNotationOnly
                ? "This page appears to be standard notation, not six-string TAB."
                : "No readable six-string TAB on this page. The scan couldn't read the fret notation.";
            return;
        }
        tabDraftStatus = $"{page.Staves.Length} candidate staff rows found.";
        var opening = tabWholeReading?.Pages[0].Settings;
        if ((page.Settings.Bpm ?? opening?.Bpm) is { } bpm) tabDraftBpm = bpm;
        if ((page.Settings.Capo ?? opening?.Capo) is { } capo) tabDraftCapo = capo;
        if ((page.Settings.Tuning ?? opening?.Tuning) is { } tuning) tabDraftTuning = tuning;
        ChooseTabDraftStaff(0);
    }

    private void ChooseTabDraftStaff(int index)
    {
        if (tabDraft is not { } draft) return;
        if (lessonPlayer.Path == DraftPreview && myTabToolActive) StopTools();
        tabDraftPreviewScore = null;
        tabWholePreview = false;
        tabDraftStaff = index;
        var rhythm = draft.Rhythms[index];
        tabDraftColumns = draft.Staves[index].Columns.Select((col, i) =>
            new TabGuideBuilder.Column(col.X, col.Width, (int[])col.Frets.Clone())
            {
                Beats = rhythm.Beats[i] ?? 0,
                RestBefore = rhythm.RestsBefore[i],
            }).ToList();
        tabDraftColumn = 0;
        tabDraftFirst = 1;
        tabDraftLast = tabDraftColumns.Count;
        tabDraftLabel = $"Page {draft.Page}, staff {index + 1}";
        tabDraftReviewed = tabDraftInsert = false;
    }

    private bool DrawMyTabGuideBuilder(TabPracticeFile tab)
    {
        FinishMyTabBuilderJobs(tab);
        if (!ImGui.CollapsingHeader("Build practice guide")) return false;
        ImGui.TextWrapped("Detect frets and supported rhythm marks on one staff. " +
            "Check missed notes, unread lengths, repeats, and held voices before saving.");
        var busy = tabDraftScan is not null || tabDraftSave is not null || tabWholeScan is not null;
        ImGui.BeginDisabled(busy);
        var scanLabel = tabDraft is { } displayed && displayed.Pdf == tab.Pdf && displayed.Page == myTabPage
            ? "Rescan page (discard draft)" : "Detect notation on this page";
        if (ImGui.SmallButton(scanLabel))
        {
            var page = myTabPage;
            if (tabWholeReading is { } cached && tabDraft?.Page != page && page <= cached.Pages.Length)
                ApplyTabDraftPage(TabDraftPage.From(tab.Pdf, cached.Pages[page - 1]));
            else
            {
                tabDraftStatus = $"Checking notation on page {page}...";
                tabDraftScan = Task.Run(() => TabDraftPage.From(tab.Pdf, TabPdfScan.ReadPage(pluginDirectory, tab.Pdf, page)));
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Scan whole PDF"))
        {
            tabDraftStatus = "Scanning all pages locally...";
            tabWholeReading = null;
            tabWholeScan = (tab.Pdf, Task.Run(() => TabPdfScan.ReadScore(pluginDirectory, tab.Pdf)));
        }
        ImGui.EndDisabled();
        if (tabWholeReading is { } whole)
        {
            ImGui.TextWrapped($"{whole.Pages.Length} pages · {whole.StaffCount} readable TAB rows · " +
                $"{whole.TimedColumns}/{whole.ColumnCount} detected columns timed");
            if (whole.PreviewProblem is { } problem) ImGui.TextWrapped("Whole-score preview unavailable: " + problem);
            else
            {
                ImGui.BeginDisabled(ReaderAudioLoading);
                if (ImGui.SmallButton("Preview detected score (unreviewed)"))
                {
                    try { PlayDetectedTabScore(whole); }
                    catch (Exception e) { tabDraftStatus = e.Message; }
                }
                ImGui.EndDisabled();
            }
        }
        if (tabDraft is not { Staves.Length: > 0 } draft || draft.Pdf != tab.Pdf || draft.Page != myTabPage || busy)
        {
            if (tabDraftStatus.Length > 0) ImGui.TextWrapped(tabDraftStatus);
            return false;
        }

        if (ImGui.BeginCombo("Staff", $"{tabDraftStaff + 1} of {draft.Staves.Length}"))
        {
            for (var i = 0; i < draft.Staves.Length; i++)
                if (ImGui.Selectable($"Staff {i + 1} · {draft.Staves[i].Columns.Length} detected columns", i == tabDraftStaff)
                    && i != tabDraftStaff) ChooseTabDraftStaff(i);
            ImGui.EndCombo();
        }
        var reading = draft.Rhythms[tabDraftStaff];
        ImGui.TextDisabled($"Read rhythm for {reading.Beats.Count(beat => beat is not null)}/{reading.Beats.Length} columns");
        if (reading.HasUnsupportedMarks) ImGui.TextWrapped("Repeat, rest, or other signs require review. Preview plays one pass.");
        var fallbackSettings = tabWholeReading?.Pages[0].Settings;
        if ((draft.Settings.Bpm ?? fallbackSettings?.Bpm) is null ||
            (draft.Settings.Capo ?? fallbackSettings?.Capo) is null ||
            (draft.Settings.Tuning ?? fallbackSettings?.Tuning) is null)
            ImGui.TextWrapped("Some tempo, tuning, or capo markings weren't read. Check the values below.");

        ImGui.SetNextItemWidth(100 * ReaderScale);
        var changed = ImGui.InputInt("BPM", ref tabDraftBpm, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * ReaderScale);
        changed |= ImGui.InputInt("Capo", ref tabDraftCapo, 0);
        changed |= ImGui.InputText("Open strings (low to high, MIDI)", ref tabDraftTuning, 80);
        if (ImGui.SmallButton("Standard tuning")) { tabDraftTuning = "40 45 50 55 59 64"; changed = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Drop D")) { tabDraftTuning = "38 45 50 55 59 64"; changed = true; }

        var index = tabDraftColumn + 1;
        if (ImGui.SliderInt("Review column", ref index, 1, tabDraftColumns.Count)) tabDraftColumn = index - 1;
        var column = tabDraftColumns[tabDraftColumn];
        ImGui.TextDisabled("Strings 1–6 (high to low); -1 = silent");
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.SetNextItemWidth(43 * ReaderScale);
            changed |= ImGui.InputInt($"##draft_fret{i}", ref column.Frets[i], 0);
        }
        var length = column.Beats;
        var rest = column.RestBefore;
        ImGui.SetNextItemWidth(85 * ReaderScale);
        if (ImGui.InputFloat("Length (beats; 0 = unset)", ref length, 0, 0, "%.3g"))
        { column.Beats = length; changed = true; }
        ImGui.SetNextItemWidth(85 * ReaderScale);
        if (ImGui.InputFloat("Rest before (beats)", ref rest, 0, 0, "%.3g"))
        { column.RestBefore = rest; changed = true; }
        ImGui.Checkbox("Add missing column on next TAB click", ref tabDraftInsert);
        if (tabDraftColumns.Count > 1 && ImGui.SmallButton("Delete selected column"))
        {
            tabDraftColumns.RemoveAt(tabDraftColumn);
            tabDraftColumn = Math.Min(tabDraftColumn, tabDraftColumns.Count - 1);
            tabDraftFirst = 1; tabDraftLast = tabDraftColumns.Count;
            column = tabDraftColumns[tabDraftColumn];
            changed = true;
        }

        if (ImGui.TreeNode("Passage range and label"))
        {
            changed |= ImGui.SliderInt("First column", ref tabDraftFirst, 1, tabDraftColumns.Count);
            changed |= ImGui.SliderInt("Last column", ref tabDraftLast, 1, tabDraftColumns.Count);
            changed |= ImGui.InputText("Passage label", ref tabDraftLabel, 151);
            ImGui.TreePop();
        }
        if (changed) InvalidateTabDraftPreview();

        int[] Tuning() => tabDraftTuning.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(text =>
            int.TryParse(text, out var value) ? value : -1).ToArray();
        TabPdfGuide.Document BuildDraft() => TabGuideBuilder.Build(draft.Hash, draft.Page,
            draft.Staves[tabDraftStaff].Staff, Tuning(), tabDraftCapo, tabDraftBpm, tabDraftLabel,
            tabDraftColumns.Skip(tabDraftFirst - 1).Take(tabDraftLast - tabDraftFirst + 1));
        void Preview(Action start, TabPdfGuide? score = null)
        {
            StartTabDraftPreview(start, score, false);
        }
        void Try(Action action)
        {
            try { action(); }
            catch (Exception e) { tabDraftStatus = e.Message; }
        }
        ImGui.BeginDisabled(ReaderAudioLoading);
        if (ImGui.SmallButton("Hear selected chord")) Try(() =>
        {
            var notes = TabGuideBuilder.Audition(column, Tuning(), tabDraftCapo);
            Preview(() => lessonPlayer.LoadTabDraftNotes(DraftPreview, notes));
        });
        ImGui.SameLine();
        if (ImGui.SmallButton("Preview passage"))
            Try(() =>
            {
                var preview = TabPdfGuide.FromDocument(BuildDraft());
                Preview(() => lessonPlayer.LoadTabGuide(DraftPreview, preview), preview);
            });
        ImGui.EndDisabled();
        if (!tabWholePreview && lessonPlayer.Path == DraftPreview && (lessonPlayer.Playing || lessonPlayer.Loading))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop preview")) StopTools();
        }
        else if (lessonPlayer.Path == DraftPreview && !lessonPlayer.Ready)
            ImGui.TextWrapped(lessonPlayer.Status);
        ImGui.Checkbox("I checked the frets, rhythm, tuning, and capo against the PDF", ref tabDraftReviewed);
        var existing = tab.Guide is not null;
        if (existing && ImGui.BeginCombo("Existing guide", tabDraftSaveMode switch
            {
                TabPdfGuide.SaveMode.Append => "Append this passage with backup",
                TabPdfGuide.SaveMode.Replace => "Replace the guide with backup",
                _ => "Keep existing guide",
            }))
        {
            if (ImGui.Selectable("Keep existing guide")) tabDraftSaveMode = TabPdfGuide.SaveMode.Create;
            if (ImGui.Selectable("Append this passage with backup")) tabDraftSaveMode = TabPdfGuide.SaveMode.Append;
            if (ImGui.Selectable("Replace the guide with backup")) tabDraftSaveMode = TabPdfGuide.SaveMode.Replace;
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(!tabDraftReviewed || (existing && tabDraftSaveMode == TabPdfGuide.SaveMode.Create) || tabDraftSave is not null);
        if (ImGui.SmallButton("Save reviewed guide"))
            Try(() =>
            {
                var document = BuildDraft();
                var mode = existing ? tabDraftSaveMode : TabPdfGuide.SaveMode.Create;
                tabDraftStatus = "Saving reviewed guide...";
                tabDraftSave = (tab.Pdf, Task.Run(() => TabPdfGuide.SaveReviewed(tab.Pdf, document, mode)));
            });
        ImGui.EndDisabled();
        if (tabDraftStatus.Length > 0) ImGui.TextWrapped(tabDraftStatus);
        return true;
    }

    private void PlayDetectedTabScore(TabPdfScan.Score whole)
    {
        var score = whole.BuildPreview();
        myTabPage = 1;
        myTabFollowScore = true;
        StartTabDraftPreview(() => lessonPlayer.LoadTabGuide(DraftPreview, score), score, whole: true);
    }

    private void StartTabDraftPreview(Action start, TabPdfGuide? score, bool whole)
    {
        EnterTool();
        myTabToolActive = true;
        tabDraftPreviewScore = score;
        tabWholePreview = whole;
        ResetMyTabScoreSelection();
        playingMyTabScore = whole ? score : null;
        start();
        queuedLessonCue = (new LessonAudioCue("", 0, null), true);
        initialiseLoop = true;
    }

    private void DrawMyTabDraftOverlay(string pdf, Vector2 origin, Vector2 size)
    {
        if (tabDraft is not { } draft || draft.Pdf != pdf || draft.Page != myTabPage) return;
        var staff = draft.Staves[tabDraftStaff].Staff;
        Vector2 At(double x, double y) => origin + size * new Vector2((float)x, (float)y);
        var min = At(staff.Left + (staff.Right - staff.Left) * .06, staff.Top);
        var max = At(staff.Right, staff.Bottom);
        var saved = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##tab_draft_review", max - min))
        {
            var x = (ImGui.GetMousePos().X - origin.X) / size.X;
            double Distance(TabGuideBuilder.Column column) => Math.Abs(column.X + column.Width * .5 - x);
            if (tabDraftInsert && tabDraftColumns.Count < 256 && tabDraftColumns.All(column => Distance(column) > .003))
            {
                var left = Math.Clamp(x - .002, (min.X - origin.X) / size.X, staff.Right - .004);
                var added = new TabGuideBuilder.Column(left, .004,
                    [-1, -1, -1, -1, -1, -1]);
                var index = tabDraftColumns.FindIndex(column => column.X > added.X);
                tabDraftColumn = index < 0 ? tabDraftColumns.Count : index;
                tabDraftColumns.Insert(tabDraftColumn, added);
                tabDraftFirst = 1; tabDraftLast = tabDraftColumns.Count;
                InvalidateTabDraftPreview();
            }
            else tabDraftColumn = tabDraftColumns.IndexOf(tabDraftColumns.MinBy(Distance)!);
            tabDraftInsert = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tabDraftInsert ? "Click the missing note's column." : "Click a column to review its frets and timing.");
        ImGui.SetCursorScreenPos(saved);

        var draw = ImGui.GetWindowDrawList();
        var scale = ReaderScale;
        for (var i = 0; i < tabDraftColumns.Count; i++)
        {
            var column = tabDraftColumns[i];
            var color = ImGui.GetColorU32(Rgb(i == tabDraftColumn ? 0xc16904u : column.Beats > 0 ? 0x31786cu : 0x7c6e55u));
            var x = column.X + column.Width / 2;
            draw.AddCircleFilled(At(x, staff.Top) - new Vector2(0, 3 * scale), (i == tabDraftColumn ? 3 : 2) * scale, color);
            if (i == tabDraftColumn)
                draw.AddRect(At(column.X, staff.Top) - new Vector2(2, 1) * scale,
                    At(column.X + column.Width, staff.Bottom) + new Vector2(2, 1) * scale, color);
        }
        if (tabDraftPreviewScore is { } score && lessonPlayer.Path == DraftPreview && lessonPlayer.Ready &&
            lessonPlayer.Playing && score.RowAt(lessonPlayer.AudiblePosition) is { } row)
        {
            var x = row.Cursor.XAt(lessonPlayer.AudiblePosition);
            var top = At(x, staff.Top) - new Vector2(0, 4) * scale;
            var bottom = At(x, staff.Bottom) + new Vector2(0, 4) * scale;
            draw.AddLine(top, bottom, ImGui.GetColorU32(Rgb(0x523612)), 5 * scale);
            draw.AddLine(top, bottom, ImGui.GetColorU32(Rgb(0xffc65d)), 3 * scale);
        }
    }

    private void InvalidateTabDraftPreview()
    {
        tabDraftReviewed = false;
        if (tabDraftPreviewScore is not null && lessonPlayer.Path == DraftPreview && myTabToolActive) StopTools();
        tabDraftPreviewScore = null;
    }
}
