using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record ReaderAudio(int Exercise, LessonTrack Track, int Seconds, int? EndSeconds);

    private ReaderAudio? activeReaderAudio;
    private ReaderAudio? readerScrubAudio;
    private bool readerRevealedStaff;
    private string readerAudioError = "";

    private bool ReaderAudioLoading => lessonPlayer.Loading || scanningLessons is not null || loadingCatalog is not null;

    private void StartPrintedCue(ReaderAudio audio)
    {
        if (ReaderAudioLoading) return;
        try
        {
            LoadLessonTrack(audio.Track, new LessonAudioCue("", audio.Seconds, audio.EndSeconds), playWhenReady: true);
            activeReaderAudio = audio;
            readerRevealedStaff = false;
        }
        catch (Exception e)
        {
            readerAudioError = $"Could not start exercise audio: {e.Message}";
        }
    }

    private static string ReaderTime(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private void DrawReaderAudioStatus(ReaderAudio? audio)
    {
        if (audio is null && tablatureAudioSeconds is not null)
            ImGui.TextDisabled("Exercise audio needs a matching weekly MP3 and a readable cue range.");
        else if (audio is not null && activeReaderAudio == audio && lessonPlayer.Loading)
            ImGui.TextDisabled("Loading local exercise audio...");
        else if (readerAudioError.Length > 0) ImGui.TextWrapped(readerAudioError);
        else if (audio is not null && activeReaderAudio == audio &&
            (lessonPlayer.Status.StartsWith("Could not play", StringComparison.Ordinal) ||
             lessonPlayer.Status.StartsWith("Could not seek", StringComparison.Ordinal)))
            ImGui.TextWrapped(lessonPlayer.Status);
        else if (audio is not null && tablatureStaff is { } staff)
        {
            var ready = lessonPlayer.Ready && activeReaderAudio == audio;
            var end = audio.EndSeconds ?? (ready ? lessonPlayer.Duration : double.PositiveInfinity);
            var mapping = new LessonScoreCursor(staff, audio.Seconds, end, tablaturePlayhead);
            var label = "Audio only  /  Drag the strip below TAB to seek";
            if (mapping.NoteSynced)
            {
                var position = ready ? lessonPlayer.AudiblePosition : mapping.MusicStart;
                label = position < mapping.MusicStart ? "Intro / count-in  ·  Click a fret to skip ahead"
                    : position >= mapping.MusicEnd ? "Phrase ended  ·  Click a fret to seek"
                    : "Note-synced  /  Drag across TAB to seek";
            }
            ImGui.TextDisabled(label);
            if (!mapping.NoteSynced && ImGui.IsItemHovered())
                ImGui.SetTooltip(tablaturePlayheadProblem ?? "The note timings don't match this TAB row.");
        }
        else if (audio is not null) ImGui.TextDisabled("This TAB row could not be located for seeking.");
    }

    private void DrawReaderAudioOverlay(LessonTablatureCrop? view, LessonTablatureCrop box, ReaderAudio audio,
        Vector2 imageOrigin, Vector2 imageSize)
    {
        // The hit area covers the printed speaker plus its timestamp, not the score.
        // Transform from page space so it follows full-page view, zoom, and scroll.
        Vector2 Project(double x, double y) => imageOrigin + imageSize * new Vector2(
            (float)((x - (view?.X ?? 0)) / (view?.Width ?? 1)),
            (float)((y - (view?.Y ?? 0)) / (view?.Height ?? 1)));
        var ready = activeReaderAudio == audio && lessonPlayer.Ready && readerAudioError.Length == 0;
        var end = audio.EndSeconds ?? lessonPlayer.Duration;
        var position = ready ? lessonPlayer.AudiblePosition : audio.Seconds;
        var draw = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        if (ready && end > audio.Seconds && tablatureStaff is { } staff)
        {
            var scale = ReaderScale;
            var mapping = new LessonScoreCursor(staff, audio.Seconds, end, tablaturePlayhead);
            var rowMin = Project(mapping.Left, staff.Top);
            var rowMax = Project(mapping.Right, staff.Bottom);
            var bottomEdge = Project(mapping.Right, box.Y + box.Height).Y;
            var stripY = (rowMax.Y + bottomEdge) * .5f;
            if (!readerRevealedStaff)
            {
                var overflow = (mapping.NoteSynced ? rowMax.Y : stripY) + 5 * scale - draw.GetClipRectMax().Y;
                if (overflow > 0 && ImGui.GetScrollMaxY() > ImGui.GetScrollY())
                    ImGui.SetScrollY(Math.Min(ImGui.GetScrollMaxY(), ImGui.GetScrollY() + overflow));
                readerRevealedStaff = true;
            }
            var hitMin = Vector2.Max(new Vector2(rowMin.X, mapping.NoteSynced
                ? rowMin.Y - 8 * scale : Math.Max(rowMax.Y, stripY - 6 * scale)), imageOrigin);
            var hitMax = Vector2.Min(new Vector2(rowMax.X, mapping.NoteSynced
                ? rowMax.Y + 5 * scale : Math.Min(bottomEdge, stripY + 6 * scale)), imageOrigin + imageSize);
            if (hitMin.X < hitMax.X && hitMin.Y < hitMax.Y)
            {
                ImGui.SetCursorScreenPos(hitMin);
                ImGui.BeginDisabled(ReaderAudioLoading);
                ImGui.InvisibleButton("##score_audio_scrub", hitMax - hitMin);
                double MouseTime()
                {
                    var x = Math.Clamp((ImGui.GetMousePos().X - rowMin.X) / (rowMax.X - rowMin.X), 0, 1);
                    return mapping.TimeAt(mapping.Left + x * (mapping.Right - mapping.Left));
                }
                if (ImGui.IsItemActivated()) readerScrubAudio = audio;
                if (readerScrubAudio == audio && ImGui.IsItemActive())
                {
                    position = MouseTime();
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip($"Seek to {Math.Max(0, position - audio.Seconds):0.00} s");
                }
                else if (readerScrubAudio == audio && ImGui.IsItemDeactivated())
                {
                    if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        position = MouseTime();
                        lessonPlayer.SeekFromScore(position);
                    }
                    readerScrubAudio = null;
                }
                else if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(mapping.NoteSynced
                        ? "Click a fret to seek to that note, or drag across TAB."
                        : "Scrub the recording by elapsed time. This exercise has no matching reviewed note map.");
                }
                ImGui.EndDisabled();
                ImGui.SetCursorScreenPos(cursor);
            }
            var playheadX = Project(mapping.XAt(position), staff.Top).X;
            draw.PushClipRect(imageOrigin, imageOrigin + imageSize, true);
            if (mapping.ShowsNoteAt(position))
            {
                var top = new Vector2(playheadX, rowMin.Y - 5 * scale);
                var bottom = new Vector2(playheadX, rowMax.Y + 3 * scale);
                draw.AddLine(top, bottom, ImGui.GetColorU32(Rgb(0x523612)), 5 * scale);
                draw.AddLine(top, bottom, ImGui.GetColorU32(Rgb(0xffc65d)), 3 * scale);
                draw.AddTriangleFilled(top + new Vector2(-5, -5) * scale, top + new Vector2(5, -5) * scale,
                    top + new Vector2(0, 2) * scale, ImGui.GetColorU32(Rgb(0x91651e)));
            }
            else if (!mapping.NoteSynced)
            {
                var lineStart = new Vector2(rowMin.X, stripY);
                var lineEnd = new Vector2(rowMax.X, stripY);
                var thumb = new Vector2(playheadX, stripY);
                draw.AddLine(lineStart, lineEnd, ImGui.GetColorU32(Rgb(0xb2a489)), 2 * scale);
                draw.AddLine(lineStart, thumb, ImGui.GetColorU32(Rgb(0x91651e)), 2 * scale);
                draw.AddCircleFilled(thumb, 3 * scale, ImGui.GetColorU32(Rgb(0x91651e)));
            }
            draw.PopClipRect();
        }
        var min = Project(box.X, box.Y + box.Height * .74);
        var max = Project(box.X + box.Width * .06, box.Y + box.Height);
        if (min.X < imageOrigin.X - .5f || min.Y < imageOrigin.Y - .5f ||
            max.X > imageOrigin.X + imageSize.X + .5f || max.Y > imageOrigin.Y + imageSize.Y + .5f)
            return;

        ImGui.SetCursorScreenPos(min);
        ImGui.BeginDisabled(ReaderAudioLoading);
        if (ImGui.InvisibleButton("##printed_audio_cue", max - min))
        {
            if (!ready) StartPrintedCue(audio);
            else if (lessonPlayer.Playing) lessonPlayer.Pause();
            else StartLessonPlayback();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var action = ready && lessonPlayer.Playing ? "Pause" : "Play";
            var elapsed = ready ? $"  {ReaderTime(Math.Max(0, position - audio.Seconds))} / {ReaderTime(Math.Max(0, end - audio.Seconds))}" : "";
            ImGui.SetTooltip($"{action} exercise {audio.Exercise}{elapsed}");
            draw.AddRect(min, max, ImGui.GetColorU32(ReaderGold), 3 * ReaderScale, thickness: 2 * ReaderScale);
        }
        ImGui.EndDisabled();
        ImGui.SetCursorScreenPos(cursor);
    }
}
