using System.Runtime.InteropServices;

namespace FFTrueBard.Lessons;

internal static class LessonPdfRenderer
{
    internal const string LibraryFile = "fftruebard-pdfium.dll";
    private const int MaximumPdfBytes = 128 * 1024 * 1024;
    private const int LongEdge = 1800;
    private const int Bgra = 4;
    private static readonly object RenderLock = new();

    internal sealed record Image(int Width, int Height, byte[] Pixels, int? AudioSeconds, int? NextAudioSeconds = null);

    public static unsafe Image Render(string pluginDirectory, string path, int pageNumber, LessonTablatureCrop? crop,
        LessonTablatureCrop? audioCrop = null, LessonTablatureEntry? nextAudio = null)
    {
        path = LessonLibrary.Normalize(path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Select an existing local PDF in Lesson files.");
        crop?.Validate();
        audioCrop?.Validate();

        // PDFium needs these bytes to stay pinned until we close the document. Loading the
        // bytes ourselves also supports Unicode paths and keeps PDFium away from the filesystem.
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > MaximumPdfBytes)
            throw new InvalidDataException("The preview needs a non-empty PDF of at most 128 MiB.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);

        // PDFium isn't thread-safe. Only our bundled module is loaded, not the host's
        // renderer (if any), and the native library is released after each render.
        lock (RenderLock)
        {
            var library = NativeLibrary.Load(Path.Combine(pluginDirectory, LibraryFile),
                typeof(LessonPdfRenderer).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            try
            {
                nint Export(string name) => NativeLibrary.GetExport(library, "FPDF_" + name);
                var initialize = (delegate* unmanaged<void>)Export("InitLibrary");
                var shutdown = (delegate* unmanaged<void>)Export("DestroyLibrary");
                initialize();
                try
                {
                    var open = (delegate* unmanaged<byte*, nuint, byte*, nint>)Export("LoadMemDocument64");
                    var close = (delegate* unmanaged<nint, void>)Export("CloseDocument");
                    fixed (byte* input = bytes)
                    {
                        var document = open(input, (nuint)bytes.Length, null);
                        if (document == 0)
                            throw new InvalidDataException("The PDF could not be opened. It may be damaged or password-protected.");
                        try
                        {
                            var count = ((delegate* unmanaged<nint, int>)Export("GetPageCount"))(document);
                            if (pageNumber < 1 || pageNumber > count)
                                throw new InvalidDataException($"Page {pageNumber} is outside this PDF ({count} pages).");
                            var loadPage = (delegate* unmanaged<nint, int, nint>)Export("LoadPage");
                            var closePage = (delegate* unmanaged<nint, void>)Export("ClosePage");
                            var page = loadPage(document, pageNumber - 1);
                            if (page == 0)
                                throw new InvalidDataException("This PDF page could not be loaded.");
                            try
                            {
                                var image = RenderPage(library, page, crop, audioCrop);
                                if (image.AudioSeconds is null || nextAudio is null) return image;
                                if (nextAudio.PdfPage == pageNumber)
                                    return image with { NextAudioSeconds = ReadPrintedAudio(library, page, nextAudio.Crop) };
                                var next = loadPage(document, nextAudio.PdfPage - 1);
                                if (next == 0) return image;
                                try { return image with { NextAudioSeconds = ReadPrintedAudio(library, next, nextAudio.Crop) }; }
                                finally { closePage(next); }
                            }
                            finally { closePage(page); }
                        }
                        finally { close(document); }
                    }
                }
                finally { shutdown(); }
            }
            finally { NativeLibrary.Free(library); }
        }
    }

    private static unsafe Image RenderPage(nint library, nint page, LessonTablatureCrop? crop, LessonTablatureCrop? audioCrop)
    {
        nint Export(string name) => NativeLibrary.GetExport(library, "FPDF" + name);
        var pageWidth = ((delegate* unmanaged<nint, float>)Export("_GetPageWidthF"))(page);
        var pageHeight = ((delegate* unmanaged<nint, float>)Export("_GetPageHeightF"))(page);
        if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
            throw new InvalidDataException("This PDF page has invalid dimensions.");

        var sourceWidth = (crop?.Width ?? 1) * pageWidth;
        var sourceHeight = (crop?.Height ?? 1) * pageHeight;
        var scale = LongEdge / Math.Max(sourceWidth, sourceHeight);
        static int Pixels(double size) => Math.Max(1, (int)Math.Round(size));
        var width = Pixels(sourceWidth * scale);
        var height = Pixels(sourceHeight * scale);
        var fullWidth = Pixels(pageWidth * scale);
        var fullHeight = Pixels(pageHeight * scale);
        // Render the scaled page offset into the smaller bitmap. PDFium clips to the bitmap
        // bounds; even a small exercise crop needs only a single bounded allocation.
        var x = -(int)Math.Round((crop?.X ?? 0) * fullWidth);
        var y = -(int)Math.Round((crop?.Y ?? 0) * fullHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var create = (delegate* unmanaged<int, int, int, byte*, int, nint>)Export("Bitmap_CreateEx");
        var fill = (delegate* unmanaged<nint, int, int, int, int, uint, int>)Export("Bitmap_FillRect");
        var draw = (delegate* unmanaged<nint, nint, int, int, int, int, int, int, void>)Export("_RenderPageBitmap");
        var destroy = (delegate* unmanaged<nint, void>)Export("Bitmap_Destroy");
        fixed (byte* output = pixels)
        {
            var bitmap = create(width, height, Bgra, output, stride);
            if (bitmap == 0)
                throw new InvalidOperationException("Could not allocate a PDF preview bitmap.");
            try
            {
                if (fill(bitmap, 0, 0, width, height, 0xffffffff) == 0)
                    throw new InvalidOperationException("Could not initialize the PDF preview bitmap.");
                draw(bitmap, page, x, y, fullWidth, fullHeight, 0, 0);
            }
            finally { destroy(bitmap); }
        }
        var seconds = audioCrop is null ? null : ReadPrintedAudio(library, page, audioCrop);
        return new(width, height, pixels, seconds);
    }

    private static unsafe int? ReadPrintedAudio(nint library, nint page, LessonTablatureCrop crop)
    {
        var toPage = (delegate* unmanaged<nint, int, int, int, int, int, int, int, double*, double*, int>)
            NativeLibrary.GetExport(library, "FPDF_DeviceToPage");
        // The PDF's crop box can offset its coordinate origin. Ask PDFium to map
        // the printed label rectangle from a normalized rendering to page space.
        const int grid = 100_000;
        double left, top, right, bottom;
        if (toPage(page, 0, 0, grid, grid, 0, (int)(crop.X * grid), (int)((crop.Y + crop.Height * .87) * grid), &left, &top) == 0 ||
            toPage(page, 0, 0, grid, grid, 0, (int)((crop.X + crop.Width * .057) * grid), (int)((crop.Y + crop.Height) * grid), &right, &bottom) == 0)
            return null;

        var load = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(library, "FPDFText_LoadPage");
        var close = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "FPDFText_ClosePage");
        var read = (delegate* unmanaged<nint, double, double, double, double, char*, int, int>)
            NativeLibrary.GetExport(library, "FPDFText_GetBoundedText");
        var text = load(page);
        if (text == 0) return null;
        try
        {
            // The known book prints m:ss in the bottom of the weekday column.
            Span<char> buffer = stackalloc char[24];
            int count;
            fixed (char* output = buffer)
                count = read(text, left, top, right, bottom, output, buffer.Length);
            if (count <= 0 || count >= buffer.Length) return null;
            var label = buffer[..count].TrimEnd('\0').Trim();
            var colon = label.IndexOf(':');
            if (colon is < 1 or > 2 || label.Length != colon + 3) return null;
            foreach (var digit in label[..colon])
                if (!char.IsAsciiDigit(digit)) return null;
            var tens = label[colon + 1];
            var ones = label[colon + 2];
            return tens is >= '0' and <= '5' && char.IsAsciiDigit(ones)
                ? int.Parse(label[..colon]) * 60 + (tens - '0') * 10 + ones - '0'
                : null;
        }
        finally { close(text); }
    }
}
