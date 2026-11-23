using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private sealed record TablatureRequest(string Path, int Page, LessonTablatureCrop? Crop);

    private TablatureRequest? tablatureRequest;
    private Task<IDalamudTextureWrap>? tablatureLoad;
    private IDalamudTextureWrap? tablatureTexture;
    private string tablatureError = "";
    private bool tablatureNeedsLoad;
    private bool tablatureFullPage;
    private int tablatureRevision;
    private int tablatureManualInput = 1;
    private int tablatureManualPage;
    private int tablatureZoom = 100;

    private void DrawLessonTablature()
    {
        var path = lessonLibrary?.Paths.Pdf ?? config.LessonPdf;
        if (string.IsNullOrEmpty(path))
        {
            ImGui.TextWrapped("Select your local lesson PDF to view its tablature here.");
            if (ImGui.Button("Open lesson files")) requestedTab = "Lesson files";
            return;
        }

        if (ImGui.Button("Open book externally")) OpenLessonPdf();
        if (textures is null)
        {
            if (lessonStatus.StartsWith("Could not open PDF", StringComparison.Ordinal))
                ImGui.TextWrapped(lessonStatus);
            ImGui.TextWrapped("In-game PDF rendering is unavailable here. You can still open the book externally.");
            return;
        }

        var lesson = SelectedCatalogExercise;
        var indexed = lessonLibrary?.Tablature?.Find(config.LessonExercise);
        var linkedPage = lesson?.PdfPage ?? indexed?.PdfPage;
        var linkedCrop = lesson?.TablatureCrop ?? (linkedPage == indexed?.PdfPage ? indexed?.Crop : null);
        var page = linkedPage ?? tablatureManualPage;
        if (linkedCrop is not null)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Full page", ref tablatureFullPage);
        }
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
            if (page < 1) return;
        }

        var crop = tablatureFullPage ? null : linkedCrop;
        var request = new TablatureRequest(path, page, crop);
        if (tablatureRequest != request)
        {
            tablatureTexture?.Dispose();
            tablatureTexture = null;
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
            return;
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 11);
        ImGui.SliderInt("Zoom", ref tablatureZoom, 100, 250, "%d%%");
        ImGui.SameLine();
        ImGui.TextDisabled(crop is null ? $"PDF page {page}" : $"Exercise excerpt · PDF page {page}");
        if (ImGui.BeginChild($"tablature_page_{tablatureRevision}", Vector2.Zero, false,
            ImGuiWindowFlags.HorizontalScrollbar))
        {
            var width = Math.Max(1, ImGui.GetWindowWidth() - ImGui.GetStyle().ScrollbarSize - 1);
            var display = width * (tablatureZoom / 100f) * texture.Size / texture.Width;
            ImGui.Image(texture.Handle, display);
        }
        ImGui.EndChild();
    }

    private void UpdateLessonTablature(TablatureRequest request)
    {
        if (tablatureLoad?.IsCompleted == true)
        {
            try
            {
                var loaded = tablatureLoad.GetAwaiter().GetResult();
                // A different page was requested while this one was rendering.
                if (tablatureNeedsLoad) loaded.Dispose();
                else tablatureTexture = loaded;
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
        // Windows renders the page off the game thread; Dalamud schedules the texture upload.
        tablatureLoad = Task.Run(async () =>
        {
            var image = await LessonPdfRenderer.RenderAsync(request.Path, request.Page, request.Crop).ConfigureAwait(false);
            return await provider.CreateFromImageAsync(image, "FFTrueBard tablature").ConfigureAwait(false);
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
            if (task.IsCompletedSuccessfully) task.Result.Dispose();
            else _ = task.Exception;
        }, TaskScheduler.Default);
    }
}
