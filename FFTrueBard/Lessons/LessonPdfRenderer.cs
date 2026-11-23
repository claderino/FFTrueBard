using System.Runtime.InteropServices;

namespace FFTrueBard.Lessons;

internal static class LessonPdfRenderer
{
    internal const string LibraryFile = "fftruebard-pdfium.dll";
    private const int MaximumPdfBytes = 128 * 1024 * 1024;
    private const int LongEdge = 1800;
    private const int Bgra = 4;
    private static readonly object RenderLock = new();

    internal sealed record Image(int Width, int Height, byte[] Pixels);

    public static unsafe Image Render(string pluginDirectory, string path, int pageNumber, LessonTablatureCrop? crop)
    {
        path = LessonLibrary.Normalize(path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Select an existing local PDF in Lesson files.");
        crop?.Validate();

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
                            try { return RenderPage(library, page, crop); }
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

    private static unsafe Image RenderPage(nint library, nint page, LessonTablatureCrop? crop)
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
        return new(width, height, pixels);
    }
}
