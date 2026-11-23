using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FFTrueBard.Lessons;

internal static class LessonPdfRenderer
{
    public static async Task<byte[]> RenderAsync(string path, int pageNumber, LessonTablatureCrop? crop)
    {
        path = LessonLibrary.Normalize(path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Select an existing local PDF in Lesson files.");
        crop?.Validate();

        var file = await StorageFile.GetFileFromPathAsync(path);
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        if (pageNumber < 1 || (uint)pageNumber > pdf.PageCount)
            throw new InvalidDataException($"Page {pageNumber} is outside this PDF ({pdf.PageCount} pages).");

        using var page = pdf.GetPage((uint)(pageNumber - 1));
        var size = page.Size;
        var source = crop is null
            ? new Rect(0, 0, size.Width, size.Height)
            : new Rect(crop.X * size.Width, crop.Y * size.Height, crop.Width * size.Width, crop.Height * size.Height);
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidDataException("The selected PDF region is empty.");

        var scale = 1800 / Math.Max(source.Width, source.Height);
        using var image = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(image, new PdfPageRenderOptions
        {
            SourceRect = source,
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
            DestinationWidth = (uint)Math.Max(1, Math.Round(source.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(source.Height * scale)),
        });

        image.Seek(0);
        using var stream = image.AsStreamForRead();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).ConfigureAwait(false);
        return output.ToArray();
    }
}
