using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private Task<TabPdfGuide>? loadingMyTabScore;
    private TabPdfGuide? myTabScore;
    private TabPdfGuide? playingMyTabScore;
    private string myTabScoreError = "";
    private bool myTabFollowScore = true;
    private TabPdfGuide.ScoreRow? myTabScrollRow;
    private TabPdfGuide.ScoreRow? myTabScrubRow;
    private double myTabScrubReference;
    private LessonScoreMark? myTabLoopTarget, myTabLoopA, myTabLoopB;
    private (LessonScoreMark A, LessonScoreMark B)? myTabActiveLoop;
    private string myTabLoopError = "";

    private void ResetMyTabScore()
    {
        if (loadingMyTabScore is { } pending)
            _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        loadingMyTabScore = myTab?.Guide is { } path && myTab is { } tab
            ? Task.Run(() => TabPdfGuide.Load(tab.Pdf, path)) : null;
        myTabScore = null;
        myTabScoreError = "";
        myTabFollowScore = true;
        ResetMyTabScoreSelection();
    }

    private void ResetMyTabScoreSelection()
    {
        myTabScrollRow = myTabScrubRow = null;
        myTabLoopTarget = myTabLoopA = myTabLoopB = null;
        myTabActiveLoop = null;
        myTabLoopError = "";
    }

    private void MoveMyTabPage(int delta)
    {
        myTabPage += delta;
        if (lessonPlayer.Playing && playingMyTabScore is not null) myTabFollowScore = false;
        myTabScrollRow = myTabScrubRow = null;
    }

    private bool DrawMyTabScoreControls(TabPracticeFile tab)
    {
        if (loadingMyTabScore is { IsCompleted: true } pending)
        {
            loadingMyTabScore = null;
            try { myTabScore = pending.GetAwaiter().GetResult(); }
            catch (Exception e) { myTabScoreError = $"Note-synced guide unavailable: {e.Message}"; }
        }
        var automatic = tabWholePreview && tabDraftPreviewScore is not null;
        var score = automatic ? tabDraftPreviewScore : myTabScore;
        var path = automatic ? DraftPreview : tab.Guide;
        if (score is null || path is null)
        {
            if (loadingMyTabScore is not null) ImGui.TextDisabled("Checking local TAB transcription...");
            else if (myTabScoreError.Length > 0) ImGui.TextWrapped(myTabScoreError);
            else ImGui.TextWrapped(tab.Midi is null
                ? "No reviewed transcription. Scan the PDF for a practice guide, or read it with the metronome."
                : "No reviewed transcription. The MIDI guide can play, but doesn't follow the printed notes.");
            return false;
        }

        var current = playingMyTabScore == score && lessonPlayer.Path == path;
        var ready = current && lessonPlayer.Ready;
        ImGui.TextWrapped(automatic ? $"{score.Coverage} (unsaved)" : $"Note-synced guide: {score.Coverage}");
        ImGui.BeginDisabled(ReaderAudioLoading);
        if (PrimaryButton(ready && lessonPlayer.Playing ? "Pause TAB audio" : "Play TAB audio"))
        {
            if (ready)
            {
                if (lessonPlayer.Playing) lessonPlayer.Pause();
                else
                {
                    myTabToolActive = true;
                    myTabScrollRow = null;
                    StartLessonPlayback();
                }
            }
            else
            {
                EnterTool();
                ResetMyTabScoreSelection();
                myTabToolActive = true;
                playingMyTabScore = score;
                lessonPlayer.LoadTabGuide(path, score);
                queuedLessonCue = (new LessonAudioCue("", 0, null), true);
                initialiseLoop = true;
            }
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready);
        if (ImGui.SmallButton("Stop TAB audio")) lessonPlayer.Stop();
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Checkbox("Follow score", ref myTabFollowScore)) myTabScrollRow = null;
        if (automatic && myTabScore is not null && ImGui.SmallButton("Use reviewed guide"))
        {
            StopTools();
            tabWholePreview = false;
            tabDraftPreviewScore = null;
            return false;
        }
        DrawLessonSpeed();

        if (ready)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{ReaderTime(lessonPlayer.AudiblePosition)} / {ReaderTime(lessonPlayer.Duration)}");
            if (myTabFollowScore && lessonPlayer.Playing && myTabScrubRow is null &&
                score.RowAt(lessonPlayer.AudiblePosition) is { } row &&
                (myTabPageCount == 0 || row.Page <= myTabPageCount) && row.Page != myTabPage)
            {
                myTabPage = row.Page;
                myTabScrollRow = null;
            }
            if (myTabLoopA is not null || myTabLoopB is not null)
            {
                if (ImGui.SmallButton("Clear TAB loop") && (!lessonPlayer.LoopEnabled || lessonPlayer.ClearScoreLoop()))
                {
                    myTabLoopA = myTabLoopB = null;
                    myTabActiveLoop = null;
                    myTabLoopError = "";
                }
                else
                {
                    ImGui.SameLine();
                    var a = myTabActiveLoop?.A ?? myTabLoopA;
                    var b = myTabActiveLoop?.B ?? myTabLoopB;
                    ImGui.TextDisabled($"A: {a?.Start.ToString("0.00") ?? "–"}   B: {b?.End.ToString("0.00") ?? "–"} s");
                }
            }
        }
        if (myTabLoopError.Length > 0) ImGui.TextWrapped(myTabLoopError);
        if (current && (!ready || lessonPlayer.Status.StartsWith("Could not", StringComparison.Ordinal)))
            ImGui.TextWrapped(lessonPlayer.Status);
        return ready;
    }

    private void DrawMyTabScoreOverlay(Vector2 origin, Vector2 size)
    {
        if (playingMyTabScore is not { } score) return;
        Vector2 At(double x, double y) => origin + size * new Vector2((float)x, (float)y);
        var draw = ImGui.GetWindowDrawList();
        var saved = ImGui.GetCursorScreenPos();
        var position = lessonPlayer.AudiblePosition;
        var rowAtTime = score.RowAt(position);
        var scale = ReaderScale;
        var loopA = myTabActiveLoop?.A ?? myTabLoopA;
        var loopB = myTabActiveLoop?.B ?? myTabLoopB;
        draw.PushClipRect(origin, origin + size, true);
        for (var i = 0; i < score.Rows.Length; i++)
        {
            var row = score.Rows[i];
            if (row.Page != myTabPage) continue;
            var staff = row.Staff;
            var mapping = row.Cursor;
            var min = At(mapping.Left, staff.Top) - new Vector2(0, 4 * scale);
            var max = At(mapping.Right, staff.Bottom) + new Vector2(0, 4 * scale);
            if (myTabFollowScore && row == rowAtTime && row != myTabScrollRow && lessonPlayer.Playing)
            {
                var viewTop = draw.GetClipRectMin().Y;
                var viewBottom = draw.GetClipRectMax().Y;
                if (max.Y > viewBottom || min.Y < viewTop)
                    ImGui.SetScrollY(Math.Clamp(ImGui.GetScrollY() + min.Y - viewTop - 10 * scale, 0, ImGui.GetScrollMaxY()));
                myTabScrollRow = row;
            }

            ImGui.PushID(i);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##my_tab_scrub", max - min);
            double ScoreX() => mapping.Left + (mapping.Right - mapping.Left) *
                Math.Clamp((ImGui.GetMousePos().X - min.X) / (max.X - min.X), 0, 1);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                myTabLoopTarget = mapping.NoteAtX(ScoreX(), position);
                myTabLoopError = "";
                ImGui.OpenPopup("##my_tab_ab");
            }
            if (ImGui.IsItemActivated())
            {
                myTabScrubRow = row;
                myTabScrubReference = position;
            }
            var cursorTime = position;
            if (myTabScrubRow == row && ImGui.IsItemActive())
            {
                cursorTime = mapping.TimeAt(ScoreX(), myTabScrubReference);
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"Seek to {cursorTime:0.00} s");
            }
            else if (myTabScrubRow == row && ImGui.IsItemDeactivated())
            {
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    cursorTime = mapping.TimeAt(ScoreX(), myTabScrubReference);
                    lessonPlayer.SeekFromScore(cursorTime);
                }
                myTabScrubRow = null;
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Click or drag to seek. Right-click a fret to set A–B.");
            }
            ImGui.SetCursorScreenPos(saved);
            DrawMyTabScoreLoopMenu();
            ImGui.PopID();

            var shade = ImGui.GetColorU32(Rgb(0x31786c) with { W = myTabActiveLoop is null ? .14f : .24f });
            if (loopA is not null && loopB is not null)
                foreach (var (left, right) in mapping.LoopSpans(loopA, loopB))
                    draw.AddRectFilled(At(left, staff.Top), At(right, staff.Bottom), shade);
            void Marker(LessonScoreMark? note, bool atEnd, string label)
            {
                if (note is null || mapping.LoopPoint(atEnd ? note.End : note.Start, atEnd) is null) return;
                var x = atEnd ? note.X + note.Width : note.X;
                var color = ImGui.GetColorU32(Rgb(0x215b51));
                draw.AddLine(At(x, staff.Top), At(x, staff.Bottom), color, 2 * scale);
                var text = ImGui.CalcTextSize(label);
                var box = (atEnd ? At(x, staff.Bottom) : At(x, staff.Top) - text) + new Vector2(atEnd ? 2 : -2, 0) * scale;
                draw.AddRectFilled(box - new Vector2(2, 0) * scale, box + text + new Vector2(2, 0) * scale, color, 2 * scale);
                draw.AddText(box, ImGui.GetColorU32(ReaderPaper), label);
            }
            Marker(loopA, false, "A");
            Marker(loopB, true, "B");
            if ((myTabScrubRow is null || myTabScrubRow == row) && mapping.ShowsNoteAt(cursorTime))
            {
                var x = mapping.XAt(cursorTime);
                var start = At(x, staff.Top) - new Vector2(0, 3 * scale);
                var end = At(x, staff.Bottom) + new Vector2(0, 3 * scale);
                draw.AddLine(start, end, ImGui.GetColorU32(Rgb(0x523612)), 5 * scale);
                draw.AddLine(start, end, ImGui.GetColorU32(Rgb(0xffc65d)), 3 * scale);
            }
        }
        draw.PopClipRect();
    }

    private void DrawMyTabScoreLoopMenu()
    {
        if (!ImGui.BeginPopup("##my_tab_ab")) return;
        if (myTabLoopTarget is { } note)
        {
            if (ImGui.MenuItem($"Set A at note start ({note.Start:0.00} s)")) myTabLoopA = note;
            if (ImGui.MenuItem($"Set B at note end ({note.End:0.00} s)")) myTabLoopB = note;
            ImGui.BeginDisabled(myTabLoopA is not { } a || myTabLoopB is not { } b || b.End - a.Start < .1);
            if (ImGui.MenuItem("Play A–B loop") && myTabLoopA is { } first && myTabLoopB is { } last)
            {
                if (lessonPlayer.SetScoreLoop(first.Start, last.End))
                {
                    myTabActiveLoop = (first, last);
                    myTabToolActive = true;
                    StartLessonPlayback();
                }
                else myTabLoopError = "The loop needs at least 0.1 s inside the generated audio.";
            }
            ImGui.EndDisabled();
        }
        ImGui.EndPopup();
    }
}
