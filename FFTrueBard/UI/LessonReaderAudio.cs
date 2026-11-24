using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record ReaderAudio(int Exercise, LessonTrack Track, int Seconds, int? EndSeconds);

    private ReaderAudio? activeReaderAudio;
    private string readerAudioError = "";

    private bool ReaderAudioLoading => lessonPlayer.Loading || scanningLessons is not null || loadingCatalog is not null;

    private void StartPrintedCue(ReaderAudio audio)
    {
        if (ReaderAudioLoading) return;
        try
        {
            LoadLessonTrack(audio.Track, new LessonAudioCue("", audio.Seconds, audio.EndSeconds), playWhenReady: true);
            activeReaderAudio = audio;
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
        else if (audio is not null && activeReaderAudio == audio && lessonPlayer.Status.StartsWith("Could not play", StringComparison.Ordinal))
            ImGui.TextWrapped(lessonPlayer.Status);
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
        draw.PushClipRect(imageOrigin, imageOrigin + imageSize, true);
        if (ready && end > audio.Seconds)
        {
            var progress = (float)Math.Clamp((position - audio.Seconds) / (end - audio.Seconds), 0, 1);
            var startLine = Project(box.X + box.Width * .07, box.Y + box.Height * .985);
            var endLine = Project(box.X + box.Width * .97, box.Y + box.Height * .985);
            draw.AddLine(startLine, endLine, ImGui.GetColorU32(Rgb(0xb2a489)), 2 * ReaderScale);
            draw.AddLine(startLine, Vector2.Lerp(startLine, endLine, progress), ImGui.GetColorU32(Rgb(0x91651e)), 3 * ReaderScale);
            if (tablaturePlayhead?.At(position) is { } note)
            {
                var low = Project(note.X, note.Y) - new Vector2(3 * ReaderScale);
                var high = Project(note.X + note.Width, note.Y + note.Height) + new Vector2(3 * ReaderScale);
                draw.AddRectFilled(low, high, ImGui.GetColorU32(new Vector4(1f, .71f, .15f, .34f)), 3 * ReaderScale);
                draw.AddRect(low, high, ImGui.GetColorU32(Rgb(0x96631c)), 3 * ReaderScale, thickness: 1.5f * ReaderScale);
            }
        }
        draw.PopClipRect();
        var min = Project(box.X, box.Y + box.Height * .74);
        var max = Project(box.X + box.Width * .06, box.Y + box.Height);
        if (min.X < imageOrigin.X - .5f || min.Y < imageOrigin.Y - .5f ||
            max.X > imageOrigin.X + imageSize.X + .5f || max.Y > imageOrigin.Y + imageSize.Y + .5f)
            return;

        var cursor = ImGui.GetCursorScreenPos();
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
