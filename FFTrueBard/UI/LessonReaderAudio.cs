using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record ReaderAudio(int Exercise, LessonTrack Track, int Seconds);

    private ReaderAudio? activeReaderAudio;
    private string readerAudioError = "";

    private ReaderAudio? FindReaderAudio(int seconds)
    {
        var exercise = config.LessonExercise;
        // Printed offsets apply to the weekly exercise MP3, not a backing track.
        var match = lessonLibrary?.FindWeeklyExerciseTrack(exercise);
        return match is null ? null : new(exercise, match, seconds);
    }

    private bool ReaderAudioLoading => lessonPlayer.Loading || scanningLessons is not null || loadingCatalog is not null;

    private void StartPrintedCue(ReaderAudio audio)
    {
        if (ReaderAudioLoading) return;
        try
        {
            LoadLessonTrack(audio.Track, new LessonAudioCue("", audio.Seconds), playWhenReady: true);
            activeReaderAudio = audio;
        }
        catch (Exception e)
        {
            readerAudioError = $"Could not start exercise audio: {e.Message}";
        }
    }

    private static string ReaderTime(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private void DrawReaderAudio(ReaderAudio? audio, int timestamp)
    {
        if (audio is null)
        {
            ImGui.TextDisabled($"Audio cue {ReaderTime(timestamp)}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The cue needs one matching weekly MP3 in the scanned Exercise MP3 folder.");
            return;
        }

        var sameTrack = lessonPlayer.Path.Equals(audio.Track.Path, StringComparison.OrdinalIgnoreCase);
        var ready = sameTrack && lessonPlayer.Ready;
        var playing = ready && lessonPlayer.Playing;
        var position = ready ? (float)lessonPlayer.Position : timestamp;
        var transport = playing ? "Pause" : ready && activeReaderAudio == audio ? "Resume" : "Play";
        var times = ready ? $"{ReaderTime(position)} / {ReaderTime(lessonPlayer.Duration)}" : $"Cue {ReaderTime(timestamp)}";
        var needsWrap = ImGui.GetContentRegionAvail().X <
            ReaderButtonWidth(transport) + ImGui.CalcTextSize(times).X + 105 * ReaderScale;

        ImGui.BeginDisabled(ReaderAudioLoading);
        if (PrimaryButton(transport + "###reader_audio_transport"))
        {
            if (playing) lessonPlayer.Pause();
            else if (ready && activeReaderAudio == audio && position < lessonPlayer.Duration - .01) StartLessonPlayback();
            else StartPrintedCue(audio);
        }
        ImGui.EndDisabled();
        if (ready)
        {
            ImGui.BeginDisabled(ReaderAudioLoading);
            if (!needsWrap) ImGui.SameLine();
            ImGui.SetNextItemWidth(needsWrap ? -1 :
                Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(times).X - ImGui.GetStyle().ItemSpacing.X));
            if (ImGui.SliderFloat("##weekly_audio_position", ref position, 0, (float)lessonPlayer.Duration, "", ImGuiSliderFlags.NoInput))
            {
                activeReaderAudio = audio;
                lessonPlayer.Seek(position);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Seek within the weekly MP3. Exercise {audio.Exercise} starts at {ReaderTime(timestamp)}.");
            ImGui.EndDisabled();
        }
        if (!needsWrap) ImGui.SameLine();
        ImGui.TextDisabled(sameTrack && lessonPlayer.Loading ? "Loading local audio..." : times);
        if (readerAudioError.Length > 0) ImGui.TextWrapped(readerAudioError);
        else if (sameTrack && lessonPlayer.Status.StartsWith("Could not play", StringComparison.Ordinal))
            ImGui.TextWrapped(lessonPlayer.Status);
    }

    private void DrawReaderAudioHotspot(LessonTablatureCrop? view, LessonTablatureCrop box, ReaderAudio audio,
        Vector2 imageOrigin, Vector2 imageSize)
    {
        // The hit area covers the printed speaker plus its timestamp, not the score.
        // Transform from page space so it follows full-page view, zoom, and scroll.
        Vector2 Project(double x, double y) => imageOrigin + imageSize * new Vector2(
            (float)((x - (view?.X ?? 0)) / (view?.Width ?? 1)),
            (float)((y - (view?.Y ?? 0)) / (view?.Height ?? 1)));
        var min = Project(box.X, box.Y + box.Height * .74);
        var max = Project(box.X + box.Width * .06, box.Y + box.Height);
        if (min.X < imageOrigin.X - .5f || min.Y < imageOrigin.Y - .5f ||
            max.X > imageOrigin.X + imageSize.X + .5f || max.Y > imageOrigin.Y + imageSize.Y + .5f)
            return;

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(min);
        ImGui.BeginDisabled(ReaderAudioLoading);
        if (ImGui.InvisibleButton("##printed_audio_cue", max - min)) StartPrintedCue(audio);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip($"Play exercise {audio.Exercise} from {ReaderTime(audio.Seconds)}");
            ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(ReaderGold), 3 * ReaderScale, thickness: 2 * ReaderScale);
        }
        ImGui.EndDisabled();
        ImGui.SetCursorScreenPos(cursor);
    }
}
