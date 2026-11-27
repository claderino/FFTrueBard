using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FFTrueBard.Lessons;

internal static class LessonPdfRenderer
{
    internal const string LibraryFile = "fftruebard-pdfium.dll";
    private const int MaximumPdfBytes = 128 * 1024 * 1024;
    private const int LongEdge = 1800;
    private const int Bgra = 4;
    private static readonly object RenderLock = new();

    internal sealed record Image(int Width, int Height, byte[] Pixels, int? AudioSeconds, int? NextAudioSeconds = null,
        LessonTabStaff? TabStaff = null)
    {
        public int PageCount { get; init; }
        public TabPdfDigit[]? TabDigits { get; init; }
        public TabPdfNotation? TabNotation { get; init; }
        public string? PdfSha256 { get; init; }
    }

    public static unsafe Image Render(string pluginDirectory, string path, int pageNumber, LessonTablatureCrop? crop,
        LessonTablatureCrop? audioCrop = null, LessonTablatureEntry? nextAudio = null, bool readTabDigits = false)
    {
        path = LessonLibrary.Normalize(path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Select an existing local PDF in Lesson files.");
        crop?.Validate();
        audioCrop?.Validate();
        if (readTabDigits && crop is not null) throw new ArgumentException("TAB detection needs a full PDF page.");

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
                                var image = RenderPage(library, page, crop, audioCrop) with { PageCount = count };
                                if (readTabDigits)
                                {
                                    var (digits, glyphs) = ReadTabText(library, page);
                                    var (strokes, arcs, contours) = ReadTabPaths(library, page);
                                    image = image with
                                    {
                                        TabDigits = digits,
                                        TabNotation = new(glyphs, strokes, arcs, contours),
                                        PdfSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                                    };
                                }
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

    private static unsafe (TabPdfDigit[] Digits, TabPdfGlyph[] Glyphs) ReadTabText(nint library, nint page)
    {
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        var open = (delegate* unmanaged<nint, nint>)Export("FPDFText_LoadPage");
        var close = (delegate* unmanaged<nint, void>)Export("FPDFText_ClosePage");
        var count = (delegate* unmanaged<nint, int>)Export("FPDFText_CountChars");
        var unicode = (delegate* unmanaged<nint, int, uint>)Export("FPDFText_GetUnicode");
        var fontInfo = (delegate* unmanaged<nint, int, byte*, nuint, int*, nuint>)Export("FPDFText_GetFontInfo");
        var origin = (delegate* unmanaged<nint, int, double*, double*, int>)Export("FPDFText_GetCharOrigin");
        var bounds = (delegate* unmanaged<nint, int, double*, double*, double*, double*, int>)Export("FPDFText_GetCharBox");
        var project = (delegate* unmanaged<nint, int, int, int, int, int, double, double, int*, int*, int>)Export("FPDF_PageToDevice");
        const int grid = 100_000;
        var text = open(page);
        if (text == 0) return ([], []);
        try
        {
            var total = count(text);
            if (total is < 1 or > 50_000) return ([], []);
            var digits = new List<TabPdfDigit>();
            var glyphs = new List<TabPdfGlyph>();
            var fontName = stackalloc byte[96];
            for (var i = 0; i < total; i++)
            {
                var code = unicode(text, i);
                if (code is < 32 or > 0xffff) continue;
                var nameLength = fontInfo(text, i, fontName, 96, null);
                var legacy = nameLength is > 1 and <= 96 &&
                    TabPdfDraft.IsLegacyGuitarFont(new ReadOnlySpan<byte>(fontName, (int)nameLength - 1));
                double ox, oy;
                int gx, gy;
                if (origin(text, i, &ox, &oy) != 0 &&
                    project(page, 0, 0, grid, grid, 0, ox, oy, &gx, &gy) != 0)
                    glyphs.Add(new((char)code, (double)gx / grid, (double)gy / grid, legacy));
                var digit = TabPdfDraft.DecodeDigit((char)code, legacy);
                if (digit < 0) continue;
                double left, right, top, bottom;
                int x0, x1, y0, y1;
                if (bounds(text, i, &left, &right, &bottom, &top) == 0 ||
                    project(page, 0, 0, grid, grid, 0, left, top, &x0, &y0) == 0 ||
                    project(page, 0, 0, grid, grid, 0, right, bottom, &x1, &y1) == 0 ||
                    x0 < 0 || x1 > grid || y0 < 0 || y1 > grid || x1 <= x0 || y1 <= y0) continue;
                digits.Add(new(digit, (double)x0 / grid, (double)y0 / grid, (double)x1 / grid, (double)y1 / grid, legacy));
            }
            return (digits.ToArray(), glyphs.ToArray());
        }
        finally { close(text); }
    }

    private static unsafe (TabPdfStroke[] Strokes, TabPdfArc[] Arcs, TabPdfContour[] Contours) ReadTabPaths(nint library, nint page)
    {
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        var count = (delegate* unmanaged<nint, int>)Export("FPDFPage_CountObjects");
        var get = (delegate* unmanaged<nint, int, nint>)Export("FPDFPage_GetObject");
        var type = (delegate* unmanaged<nint, int>)Export("FPDFPageObj_GetType");
        var formCount = (delegate* unmanaged<nint, int>)Export("FPDFFormObj_CountObjects");
        var formGet = (delegate* unmanaged<nint, int, nint>)Export("FPDFFormObj_GetObject");
        var segments = (delegate* unmanaged<nint, int>)Export("FPDFPath_CountSegments");
        var getSegment = (delegate* unmanaged<nint, int, nint>)Export("FPDFPath_GetPathSegment");
        var segmentType = (delegate* unmanaged<nint, int>)Export("FPDFPathSegment_GetType");
        var isClosed = (delegate* unmanaged<nint, int>)Export("FPDFPathSegment_GetClose");
        var point = (delegate* unmanaged<nint, float*, float*, int>)Export("FPDFPathSegment_GetPoint");
        var mode = (delegate* unmanaged<nint, int*, int*, int>)Export("FPDFPath_GetDrawMode");
        var bounds = (delegate* unmanaged<nint, float*, float*, float*, float*, int>)Export("FPDFPageObj_GetBounds");
        var matrix = (delegate* unmanaged<nint, float*, int>)Export("FPDFPageObj_GetMatrix");
        var project = (delegate* unmanaged<nint, int, int, int, int, int, double, double, int*, int*, int>)Export("FPDF_PageToDevice");
        const int objectLimit = 25_000;
        var total = count(page);
        if (total is < 1 or > objectLimit) return ([], [], []);
        const int grid = 100_000;
        var paths = new List<TabPdfStroke>();
        var arcs = new List<TabPdfArc>();
        var contours = new List<TabPdfContour>();
        var transform = stackalloc float[6];
        Span<TabPdfPoint> points = stackalloc TabPdfPoint[5];
        var work = new Stack<(nint Object, Matrix3x2 Parent, int Depth)>();
        for (var i = total - 1; i >= 0; i--)
            work.Push((get(page, i), Matrix3x2.Identity, 0));
        var visited = 0;
        var visitedSegments = 0;
        while (work.TryPop(out var item))
        {
            if (++visited > objectLimit) return ([], [], []);
            var obj = item.Object;
            if (obj == 0) continue;
            var kindOfObject = type(obj);
            if (kindOfObject is not (2 or 5) || matrix(obj, transform) == 0) continue;
            var world = new Matrix3x2(transform[0], transform[1], transform[2], transform[3], transform[4], transform[5]) * item.Parent;
            if (kindOfObject == 5)
            {
                var children = formCount(obj);
                if (item.Depth >= 12 || children > objectLimit - visited - work.Count) return ([], [], []);
                for (var i = children - 1; i >= 0; i--)
                    work.Push((formGet(obj, i), world, item.Depth + 1));
                continue;
            }
            var n = segments(obj);
            if (n < 0 || (visitedSegments += n) > 250_000) return ([], [], []);
            if (n is >= 4 and <= 128)
            {
                for (var j = 1; j < n; j++)
                {
                    var segment = getSegment(obj, j);
                    if (segment == 0 || segmentType(segment) != 1) continue;
                    float left, bottom, right, top;
                    if (bounds(obj, &left, &bottom, &right, &top) != 0)
                    {
                        var a = Vector2.Transform(new(left, top), item.Parent);
                        var b = Vector2.Transform(new(right, bottom), item.Parent);
                        var c = Vector2.Transform(new(left, bottom), item.Parent);
                        var d = Vector2.Transform(new(right, top), item.Parent);
                        var min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
                        var max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
                        int x0, y0, x1, y1;
                        if (project(page, 0, 0, grid, grid, 0, min.X, max.Y, &x0, &y0) != 0 &&
                            project(page, 0, 0, grid, grid, 0, max.X, min.Y, &x1, &y1) != 0)
                            arcs.Add(new((double)Math.Min(x0, x1) / grid, (double)Math.Min(y0, y1) / grid,
                                (double)Math.Max(x0, x1) / grid, (double)Math.Max(y0, y1) / grid, n));
                    }
                    break;
                }
            }
            // A printer can combine every clef or notehead on the page into one path.
            // The cumulative segment limit above still bounds these compound objects.
            if (n < 2) continue;
            int fill, stroke;
            if (mode(obj, &fill, &stroke) == 0) continue;
            if (stroke == 0 && fill == 0) continue;
            var written = 0;
            var curvePoints = 0;
            var leftX = 0d;
            var rightX = 0d;
            var topY = 0d;
            var bottomY = 0d;
            var previous = default(TabPdfPoint);
            var filled = fill != 0;
            void AddContour()
            {
                if (!filled || written < 4 || leftX >= rightX || topY >= bottomY) return;
                if (written <= 96)
                    contours.Add(new(leftX, topY, rightX, bottomY, written, curvePoints));
            }
            for (var j = 0; j < n; j++)
            {
                var segment = getSegment(obj, j);
                float x, y;
                int px, py;
                if (segment == 0 || point(segment, &x, &y) == 0 ||
                    project(page, 0, 0, grid, grid, 0, world.M11 * x + world.M21 * y + world.M31,
                        world.M12 * x + world.M22 * y + world.M32, &px, &py) == 0)
                {
                    written = 0;
                    break;
                }
                var kind = segmentType(segment);
                var current = new TabPdfPoint((double)px / grid, (double)py / grid);
                if (stroke != 0 && j > 0 && kind == 0)
                    paths.Add(new(false, [previous, current]));
                previous = current;
                if (kind == 2)
                {
                    // Some exporters put independent contours in a single filled path.
                    AddContour();
                    if (fill != 0 && curvePoints == 0 && written is 4 or 5)
                        paths.Add(new(true, points[..written].ToArray()));
                    written = curvePoints = 0;
                    leftX = rightX = current.X;
                    topY = bottomY = current.Y;
                }
                else
                {
                    leftX = Math.Min(leftX, current.X);
                    rightX = Math.Max(rightX, current.X);
                    topY = Math.Min(topY, current.Y);
                    bottomY = Math.Max(bottomY, current.Y);
                    if (kind == 1) curvePoints++;
                }
                if (written < points.Length) points[written] = current;
                written++;
                if (isClosed(segment) == 0) continue;
                AddContour();
                if (fill != 0 && curvePoints == 0 && written is 4 or 5)
                    paths.Add(new(true, points[..written].ToArray()));
                written = curvePoints = 0;
            }
            AddContour();
            if (fill != 0 && curvePoints == 0 && written is 4 or 5)
                paths.Add(new(true, points[..written].ToArray()));
        }
        return (paths.ToArray(), arcs.ToArray(), contours.ToArray());
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
        var staff = audioCrop is null ? null : ReadTabStaff(library, page, audioCrop);
        return new(width, height, pixels, seconds, TabStaff: staff);
    }

    private static unsafe LessonTabStaff? ReadTabStaff(nint library, nint page, LessonTablatureCrop crop)
    {
        nint Export(string symbol) => NativeLibrary.GetExport(library, symbol);
        var count = (delegate* unmanaged<nint, int>)Export("FPDFPage_CountObjects");
        var get = (delegate* unmanaged<nint, int, nint>)Export("FPDFPage_GetObject");
        var type = (delegate* unmanaged<nint, int>)Export("FPDFPageObj_GetType");
        var segments = (delegate* unmanaged<nint, int>)Export("FPDFPath_CountSegments");
        var bounds = (delegate* unmanaged<nint, float*, float*, float*, float*, int>)Export("FPDFPageObj_GetBounds");
        var project = (delegate* unmanaged<nint, int, int, int, int, int, double, double, int*, int*, int>)
            Export("FPDF_PageToDevice");
        const int grid = 100_000;
        const int pathObject = 2;
        LessonTabStaff? result = null;
        var total = count(page);
        for (var i = 0; i < total; i++)
        {
            var item = get(page, i);
            // Known book geometry: the TAB staff is a single path, with one
            // move/line pair per string. The notation above it has five pairs.
            if (item == 0 || type(item) != pathObject || segments(item) != 12) continue;
            float left, bottom, right, top;
            if (bounds(item, &left, &bottom, &right, &top) == 0 ||
                right - left < 400 || top - bottom is <= 8 or >= 55)
                continue;
            int x0, y0, x1, y1;
            if (project(page, 0, 0, grid, grid, 0, left, top, &x0, &y0) == 0 ||
                project(page, 0, 0, grid, grid, 0, right, bottom, &x1, &y1) == 0)
                continue;
            var row = new LessonTabStaff((double)x0 / grid, (double)y0 / grid, (double)x1 / grid, (double)y1 / grid);
            if (row.Left <= crop.X || row.Right >= crop.X + crop.Width ||
                row.Top <= crop.Y || row.Bottom >= crop.Y + crop.Height)
                continue;
            if (result is not null) return null; // Don't guess which line set to use.
            result = row;
        }
        return result;
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
