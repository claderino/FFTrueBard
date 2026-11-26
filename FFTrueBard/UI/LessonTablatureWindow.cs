using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record TablatureRequest(string Path, int Page, LessonTablatureCrop? Crop,
        LessonTablatureCrop? AudioCrop, LessonTablatureEntry? NextAudio, string? PlayheadTrack);
    private sealed record LoadedTablature(IDalamudTextureWrap Texture, int? AudioSeconds, double? PlaybackEndSeconds,
        LessonTabStaff? TabStaff, LessonScorePlayhead? Playhead, string? PlayheadProblem);

    private TablatureRequest? tablatureRequest;
    private Task<LoadedTablature>? tablatureLoad;
    private IDalamudTextureWrap? tablatureTexture;
    private int? tablatureAudioSeconds;
    private double? tablaturePlaybackEndSeconds;
    private LessonTabStaff? tablatureStaff;
    private LessonScorePlayhead? tablaturePlayhead;
    private string? tablaturePlayheadProblem;
    private string tablatureError = "";
    private bool tablatureNeedsLoad;
    private bool tablatureFullPage;
    private int tablatureRevision;
    private int tablatureManualInput = 1;
    private int tablatureManualPage;
    private int tablatureZoom = 100;

    private string TablaturePdfPath => lessonLibrary?.Paths.Pdf ?? config.LessonPdf;

    private (int? Page, LessonTablatureCrop? Crop) LinkedTablature()
    {
        var lesson = SelectedCatalogExercise;
        var indexed = lessonLibrary?.Tablature?.Find(config.LessonExercise);
        var page = lesson?.PdfPage ?? indexed?.PdfPage;
        return (page, lesson?.TablatureCrop ?? (page == indexed?.PdfPage ? indexed?.Crop : null));
    }

    private float TablatureToolbarWidth()
    {
        var step = ImGui.GetStyle().ItemSpacing.X;
        var width = ReaderButtonWidth("-") + ReaderButtonWidth("Fit") + ReaderButtonWidth("+") + 2 * step;
        if (LinkedTablature().Crop is not null)
            width += ReaderButtonWidth(tablatureFullPage ? "Excerpt" : "Full page") + step;
        return width;
    }

    private void DrawTablatureToolbar()
    {
        bool Action(string label, string help)
        {
            var pressed = ImGui.Button(label);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(help);
            return pressed;
        }

        if (Action("-###tab_zoom_out", "Zoom out")) tablatureZoom = Math.Max(100, tablatureZoom - 25);
        ImGui.SameLine();
        if (tablatureZoom == 100 ? PrimaryButton("Fit") : ImGui.Button("Fit")) tablatureZoom = 100;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fit the notation to the available width");
        ImGui.SameLine();
        if (Action("+###tab_zoom_in", "Zoom in")) tablatureZoom = Math.Min(250, tablatureZoom + 25);
        if (LinkedTablature().Crop is not null)
        {
            ImGui.SameLine();
            if (Action(tablatureFullPage ? "Excerpt###tab_page_mode" : "Full page###tab_page_mode",
                tablatureFullPage ? "Return to just this exercise" : "Show the complete PDF page"))
                tablatureFullPage = !tablatureFullPage;
        }
    }

    private void DrawLessonTablature()
    {
        var path = TablaturePdfPath;
        if (string.IsNullOrEmpty(path))
        {
            ImGui.TextWrapped("Select your local lesson PDF to view its tablature here.");
            if (ImGui.Button("Open lesson files")) requestedTab = "Lesson files";
            return;
        }

        if (textures is null)
        {
            if (ImGui.Button("Open book externally")) OpenLessonPdf();
            if (lessonStatus.StartsWith("Could not open PDF", StringComparison.Ordinal))
                ImGui.TextWrapped(lessonStatus);
            ImGui.TextWrapped("In-game PDF rendering is unavailable here. You can still open the book externally.");
            return;
        }

        var (linkedPage, linkedCrop) = LinkedTablature();
        var page = linkedPage ?? tablatureManualPage;
        if (lessonStatus.StartsWith("Could not open PDF", StringComparison.Ordinal))
            ImGui.TextWrapped(lessonStatus);
        if (linkedPage is null)
        {
            ImGui.TextWrapped("No PDF page is linked to this exercise. Enter the page number from your PDF viewer.");
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
            ImGui.InputInt("PDF page", ref tablatureManualInput, 0);
            ImGui.SameLine();
            ImGui.BeginDisabled(tablatureManualInput < 1);
            if (ImGui.Button("View page")) page = tablatureManualPage = tablatureManualInput;
            ImGui.EndDisabled();
            if (page < 1)
            {
                if (ImGui.Button("Open book externally")) OpenLessonPdf();
                return;
            }
        }

        var crop = tablatureFullPage ? null : linkedCrop;
        var exercise = config.LessonExercise;
        var index = lessonLibrary?.Tablature;
        var track = lessonLibrary?.FindWeeklyExerciseTrack(exercise);
        var audio = index?.FindPrintedAudio(exercise);
        var audioCrop = audio?.PdfPage == page ? audio.Crop : null;
        var nextAudio = audioCrop is not null && track is not null && exercise < track.Last
            ? index?.FindPrintedAudio(exercise + 1) : null;
        var request = new TablatureRequest(path, page, crop, audioCrop, nextAudio, audioCrop is not null ? track?.Path : null);
        if (tablatureRequest != request)
        {
            tablatureTexture?.Dispose();
            tablatureTexture = null;
            tablatureAudioSeconds = null;
            tablaturePlaybackEndSeconds = null;
            tablatureStaff = null;
            tablaturePlayhead = null;
            tablaturePlayheadProblem = null;
            readerScrubAudio = null;
            readerRevealedStaff = false;
            tablatureRequest = request;
            tablatureError = "";
            tablatureNeedsLoad = true;
            tablatureRevision++;
        }
        UpdateLessonTablature(request);

        if (tablatureTexture is not { } texture)
        {
            if (tablatureError.Length > 0)
            {
                ImGui.TextWrapped(tablatureError);
                if (ImGui.Button("Retry preview")) tablatureRequest = null;
            }
            else ImGui.TextUnformatted($"Loading local PDF page {page}...");
            if (ImGui.Button("Open book externally")) OpenLessonPdf();
            return;
        }

        var playback = tablatureAudioSeconds is { } start && track is not null &&
            (exercise == track.Last || tablaturePlaybackEndSeconds is { } end && end > start)
            ? new ReaderAudio(exercise, track, start, tablaturePlaybackEndSeconds) : null;
        if (playback is not null) DrawLessonSpeed();
        DrawScorePlate(texture, crop, audioCrop, playback);
        DrawReaderAudioStatus(playback);
        var step = SelectedCatalogExercise?.Steps.ElementAtOrDefault(lessonStep);
        if (!string.IsNullOrWhiteSpace(step) && step.Length <= 120 && ImGui.GetContentRegionAvail().Y > 3 * ImGui.GetTextLineHeightWithSpacing())
            ImGui.TextWrapped(step);
        ImGui.Separator();
        var footerWidth = ImGui.GetContentRegionAvail().X;
        ImGui.TextDisabled($"Local PDF  /  Page {page}  /  Not graded");
        if (footerWidth >= ImGui.CalcTextSize("Local PDF  /  Page 0000  /  Not graded    Open book externally").X + 20 * ReaderScale)
            ImGui.SameLine(ImGui.GetCursorPosX() + footerWidth - ImGui.CalcTextSize("Open book externally").X - 2 * ImGui.GetStyle().FramePadding.X);
        if (ImGui.SmallButton("Open book externally")) OpenLessonPdf();
    }

    private void DrawScorePlate(IDalamudTextureWrap texture, LessonTablatureCrop? crop,
        LessonTablatureCrop? audioCrop, ReaderAudio? playback)
    {
        var scale = ReaderScale;
        var padding = new Vector2(13, 10) * scale;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize - padding.X * 2 - 2);
        var display = width * (tablatureZoom / 100f) * texture.Size / texture.Width;
        var heading = ImGui.GetTextLineHeightWithSpacing();
        var plateHeight = display.Y + heading + padding.Y * 2 + 5 * scale;
        var footerSpace = ImGui.GetFrameHeightWithSpacing() * 2;
        if (playback is not null && activeReaderAudio == playback && HasReaderLoopPoints)
            footerSpace += ImGui.GetFrameHeightWithSpacing();
        var available = Math.Max(80 * scale, ImGui.GetContentRegionAvail().Y - footerSpace);
        var flags = tablatureZoom == 100 && plateHeight <= available
            ? ImGuiWindowFlags.NoScrollbar
            : ImGuiWindowFlags.HorizontalScrollbar;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ReaderPaper);
        ImGui.PushStyleColor(ImGuiCol.Border, Rgb(0xa58f67));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4 * scale);
        if (ImGui.BeginChild($"tablature_page_{tablatureRevision}", new(0, Math.Min(plateHeight, available)), true, flags))
        {
            ImGui.TextColored(Rgb(0x61513a), (crop is not null ? "GUITAR TAB" : "BOOK PAGE") +
                (tablatureZoom == 100 ? "" : $"  /  {tablatureZoom}%"));
            ImGui.Image(texture.Handle, display, Vector2.Zero, Vector2.One, ReaderPaper);
            if (audioCrop is not null && playback is not null)
                DrawReaderAudioOverlay(crop, audioCrop, playback, ImGui.GetItemRectMin(), display);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void UpdateLessonTablature(TablatureRequest request)
    {
        if (tablatureLoad?.IsCompleted == true)
        {
            try
            {
                var loaded = tablatureLoad.GetAwaiter().GetResult();
                // A different page was requested while this one was rendering.
                if (tablatureNeedsLoad) loaded.Texture.Dispose();
                else
                {
                    tablatureTexture = loaded.Texture;
                    tablatureAudioSeconds = loaded.AudioSeconds;
                    tablaturePlaybackEndSeconds = loaded.PlaybackEndSeconds;
                    tablatureStaff = loaded.TabStaff;
                    tablaturePlayhead = loaded.Playhead;
                    tablaturePlayheadProblem = loaded.PlayheadProblem;
                }
            }
            catch (Exception e)
            {
                if (!tablatureNeedsLoad)
                    tablatureError = $"Could not show PDF page {request.Page}: {e.Message}";
            }
            finally
            {
                tablatureLoad = null;
            }
        }

        if (!tablatureNeedsLoad || tablatureLoad is not null || textures is not { } provider) return;
        tablatureNeedsLoad = false;
        // Render off the game thread; Dalamud schedules the texture upload.
        var root = lessonLibrary?.Paths.Root ?? "";
        var exercise = config.LessonExercise;
        tablatureLoad = Task.Run(async () =>
        {
            var image = LessonPdfRenderer.Render(pluginDirectory, request.Path, request.Page, request.Crop, request.AudioCrop, request.NextAudio);
            var timing = request.PlayheadTrack is { } track && image.AudioSeconds is { } start
                ? LessonScorePlayhead.FindForReader(root, request.Path, exercise, request.Page, track, start, image.NextAudioSeconds)
                : new(null, null);
            var playbackEnd = (double?)image.NextAudioSeconds;
            if (timing is { Map: { } map, MeasuredEndSeconds: { } measured })
            {
                if (image.TabStaff is { } staff && LessonScoreCursor.MatchesStaff(staff, map, image.AudioSeconds!.Value, measured))
                    playbackEnd = measured;
                else
                    timing = new(null, "The measured end and note timings don't match this TAB row.");
            }
            var texture = await provider.CreateFromRawAsync(RawImageSpecification.Bgra32(image.Width, image.Height), image.Pixels,
                "FFTrueBard tablature").ConfigureAwait(false);
            return new LoadedTablature(texture, image.AudioSeconds, playbackEnd, image.TabStaff, timing.Map, timing.Problem);
        });
    }

    private void DisposeLessonTablature()
    {
        tablatureTexture?.Dispose();
        tablatureTexture = null;
        if (tablatureLoad is not { } pending) return;
        tablatureLoad = null;
        _ = pending.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Texture.Dispose();
            else _ = task.Exception;
        }, TaskScheduler.Default);
    }
}
