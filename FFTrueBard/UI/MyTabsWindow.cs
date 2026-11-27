using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record MyTabPage(IDalamudTextureWrap Texture, int Count);
    private string myTabsFolder = "";
    private bool myTabsOpened;
    private (string Folder, bool Save, Task<TabPracticeFile[]> Task)? myTabScan;
    private TabPracticeFile[] myTabs = [];
    private TabPracticeFile? myTab;
    private string myTabsStatus = "";
    private string myTabPageError = "";
    private string myTabOpenError = "";
    private int myTabPage = 1;
    private int myTabPageCount;
    private int myTabZoom = 100;
    private bool myTabToolActive;
    private (string Pdf, int Page)? shownMyTabPage;
    private ((string Pdf, int Page) Key, Task<MyTabPage> Task)? loadingMyTabPage;
    private IDalamudTextureWrap? myTabTexture;

    private void DrawMyTabs()
    {
        if (!myTabsOpened)
        {
            myTabsOpened = true;
            myTabsFolder = string.IsNullOrWhiteSpace(config.MyTabsFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "tabs")
                : config.MyTabsFolder;
            ScanMyTabs(persist: false);
        }
        FinishMyTabScan();
        ImGui.BeginDisabled(myTabScan is not null);
        LessonFolderField("TAB folder", ref myTabsFolder, path => myTabsFolder = path);
        if (ImGui.SmallButton("Save folder & scan")) ScanMyTabs(persist: true);
        ImGui.EndDisabled();
        ImGui.TextWrapped(myTabsStatus);
        if (myTabs.Length == 0) return;

        if (ImGui.BeginCombo("Song", myTab?.Title ?? "Select a TAB"))
        {
            for (var i = 0; i < myTabs.Length; i++)
            {
                var file = myTabs[i];
                if (ImGui.Selectable($"{file.Title}##tab{i}", file == myTab)) SelectMyTab(file);
            }
            ImGui.EndCombo();
        }
        if (myTab is not { } tab) return;
        var scoreReady = DrawMyTabScoreControls(tab);
        DrawMyTabGuide(tab, showMidi: myTabScore is null);
        DrawMyTabMelody(tab);
        var showDraft = DrawMyTabGuideBuilder(tab);
        DrawMyTabNavigation(tab);
        DrawMyTabPdf(tab.Pdf, scoreReady, showDraft);
    }

    private void ScanMyTabs(bool persist)
    {
        var folder = myTabsFolder;
        myTabsStatus = "Scanning local TAB files...";
        myTabScan = (folder, persist, Task.Run(() => TabPracticeFile.Scan(folder)));
    }

    private void FinishMyTabScan()
    {
        if (myTabScan is not { } scan || !scan.Task.IsCompleted) return;
        myTabScan = null;
        try
        {
            var files = scan.Task.GetAwaiter().GetResult();
            myTabs = files;
            var selected = files.FirstOrDefault(file => file.Pdf == myTab?.Pdf) ?? files.FirstOrDefault();
            if (selected == myTab && scan.Save)
            {
                if (myTabToolActive) StopTools();
                ResetMyTabBuilder();
                ResetMyTabMelody();
                ResetMyTabScore();
            }
            else SelectMyTab(selected);
            if (scan.Save)
            {
                config.MyTabsFolder = LessonLibrary.Normalize(scan.Folder);
                save();
            }
            myTabsStatus = files.Length == 0
                ? "No PDFs directly inside this folder."
                : $"{files.Length} local TABs · Not graded";
        }
        catch (Exception e) { myTabsStatus = $"Could not scan TAB folder: {e.Message}"; }
    }

    private void SelectMyTab(TabPracticeFile? tab)
    {
        if (tab == myTab) return;
        if (myTabToolActive) StopTools();
        myTab = tab;
        ResetMyTabBuilder();
        ResetMyTabMelody();
        ResetMyTabScore();
        myTabPage = 1;
        myTabPageCount = 0;
        myTabOpenError = "";
        DisposeMyTabPage();
    }

    private void DrawMyTabGuide(TabPracticeFile tab, bool showMidi)
    {
        if (showMidi && tab.Midi is not null && ImGui.CollapsingHeader("Unsynced MIDI guide"))
        {
            ImGui.TextWrapped("Synthesized plucked-string audio. This guide does not track the PDF page or grade your playing.");
            var loaded = string.Equals(lessonPlayer.Path, tab.Midi, StringComparison.OrdinalIgnoreCase);
            ImGui.BeginDisabled(ReaderAudioLoading);
            if (ImGui.Button(loaded && lessonPlayer.Ready ? "Reload MIDI guide" : "Load MIDI guide"))
            {
                EnterTool();
                myTabToolActive = true;
                lessonPlayer.LoadMidi(tab.Midi);
                initialiseLoop = true;
            }
            ImGui.EndDisabled();
            if (loaded)
            {
                if (lessonPlayer.Ready) DrawLessonPlayback(midiGuide: true);
                else ImGui.TextWrapped(lessonPlayer.Status);
            }
        }

        if (ImGui.CollapsingHeader("Metronome"))
        {
            var running = myTabToolActive && beats.Running;
            var bpm = config.MetronomeBpm;
            ImGui.BeginDisabled(running);
            ImGui.SetNextItemWidth(140 * ReaderScale);
            if (ImGui.SliderInt("Click BPM", ref bpm, 40, 160)) config.MetronomeBpm = bpm;
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(running ? "Stop click" : "Start click"))
            {
                if (running) StopTools();
                else
                {
                    EnterTool();
                    myTabToolActive = true;
                    beats.Start(Now, config.MetronomeBpm);
                    clickPlayer.Start(config.MetronomeBpm);
                }
            }
            if (running) ImGui.TextUnformatted($"Beat {Math.Max(0, beats.BeatAt(Now)) % 4 + 1} / 4");
            if (clickPlayer.Error.Length > 0) ImGui.TextWrapped(clickPlayer.Error);
        }
    }

    private void DrawMyTabNavigation(TabPracticeFile tab)
    {
        ImGui.Separator();
        if (MyTabButton("Previous", myTabPage > 1)) MoveMyTabPage(-1);
        ImGui.SameLine();
        if (MyTabButton("Next", myTabPage < myTabPageCount)) MoveMyTabPage(1);
        ImGui.SameLine();
        ImGui.TextUnformatted(myTabPageCount > 0 ? $"Page {myTabPage}/{myTabPageCount}" : $"Page {myTabPage}");
        ImGui.SameLine();
        if (MyTabButton("-##my_tab_zoom", myTabZoom > 100)) myTabZoom -= 25;
        ImGui.SameLine();
        if (ImGui.SmallButton("Fit##my_tab_zoom")) myTabZoom = 100;
        ImGui.SameLine();
        if (MyTabButton("+##my_tab_zoom", myTabZoom < 250)) myTabZoom += 25;
        ImGui.SameLine();
        if (ImGui.SmallButton("Open PDF externally"))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = tab.Pdf, UseShellExecute = true });
                myTabOpenError = "";
            }
            catch (Exception e) { myTabOpenError = $"Could not open PDF: {e.Message}"; }
        }
        if (myTabOpenError.Length > 0) ImGui.TextWrapped(myTabOpenError);
    }

    private static bool MyTabButton(string label, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        var pressed = ImGui.SmallButton(label);
        ImGui.EndDisabled();
        return pressed;
    }

    private void DrawMyTabPdf(string pdf, bool scoreReady, bool showDraft)
    {
        if (textures is not { } provider)
        {
            ImGui.TextWrapped("The in-game PDF preview is unavailable. You can still open the PDF externally.");
            return;
        }

        var wanted = (Pdf: pdf, Page: myTabPage);
        if (loadingMyTabPage is { } pending && pending.Task.IsCompleted)
        {
            loadingMyTabPage = null;
            try
            {
                var page = pending.Task.GetAwaiter().GetResult();
                if (pending.Key == wanted)
                {
                    myTabTexture = page.Texture;
                    myTabPageCount = page.Count;
                }
                else page.Texture.Dispose();
            }
            catch (Exception e)
            {
                if (pending.Key == wanted) myTabPageError = $"Could not preview TAB: {e.Message}";
            }
            if (pending.Key == wanted) shownMyTabPage = wanted;
        }

        if (shownMyTabPage != wanted)
        {
            myTabTexture?.Dispose();
            myTabTexture = null;
            myTabPageError = "";
            if (loadingMyTabPage is null)
                loadingMyTabPage = (wanted, Task.Run(async () =>
                {
                    var image = LessonPdfRenderer.Render(pluginDirectory, wanted.Pdf, wanted.Page, null);
                    var texture = await provider.CreateFromRawAsync(RawImageSpecification.Bgra32(image.Width, image.Height),
                        image.Pixels, "FFTrueBard My Tabs").ConfigureAwait(false);
                    return new MyTabPage(texture, image.PageCount);
                }));
        }

        if (myTabPageError.Length > 0)
        {
            ImGui.TextWrapped(myTabPageError);
            if (ImGui.SmallButton("Retry PDF preview")) shownMyTabPage = null;
        }
        else if (myTabTexture is { } texture)
        {
            ImGui.PushID(pdf);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ReaderPaper);
            if (ImGui.BeginChild($"my_tab_pdf_{myTabPage}", new Vector2(0, Math.Max((showDraft ? 240 : 90) * ReaderScale, ImGui.GetContentRegionAvail().Y)),
                true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize);
                var size = texture.Size / texture.Width * width * (myTabZoom / 100f);
                ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, ReaderPaper);
                if (scoreReady && tabWholePreview && lessonPlayer.Path == DraftPreview)
                    DrawMyTabScoreOverlay(ImGui.GetItemRectMin(), size);
                else if (showDraft)
                    DrawMyTabDraftOverlay(pdf, ImGui.GetItemRectMin(), size);
                else if (scoreReady)
                    DrawMyTabScoreOverlay(ImGui.GetItemRectMin(), size);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopID();
        }
        else ImGui.TextDisabled($"Loading PDF page {myTabPage}...");
    }

    private void DisposeMyTabPage()
    {
        shownMyTabPage = null;
        myTabTexture?.Dispose();
        myTabTexture = null;
        if (loadingMyTabPage is not { } pending) return;
        loadingMyTabPage = null;
        _ = pending.Task.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Texture.Dispose();
            else _ = task.Exception;
        }, TaskScheduler.Default);
    }
}
