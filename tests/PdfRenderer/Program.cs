using System.Runtime.InteropServices;
using System.Text;
using FFTrueBard.Lessons;

if (args.Length == 0)
    throw new ArgumentException("Pass the built plugin directory, optionally followed by a supported local PDF.");
var pluginDirectory = Path.GetFullPath(args[0]);

// Model a host that already owns the process-wide COM marshaller. PDF rendering
// must work without registering another instance or calling the host's marshaller.
ComWrappers.RegisterForMarshalling(new HostMarshaller());
using var folder = new ScratchDirectory();
var fixture = Path.Combine(folder.Path, "Étude 楽譜.pdf");
WriteTestPdf(fixture);

var full = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, null);
Check((full.Width, full.Height) == (1800, 900), "Full page dimensions");
CheckPixel(full, .25, .25, 0, 0, 255);
CheckPixel(full, .75, .25, 255, 255, 255);
CheckPixel(full, .75, .75, 255, 0, 0);

var crop = LessonPdfRenderer.Render(pluginDirectory, fixture, 1, new(.5, .5, .5, .5));
Check((crop.Width, crop.Height) == (1800, 900), "Excerpt dimensions");
CheckPixel(crop, .5, .5, 255, 0, 0);
Reject(() => LessonPdfRenderer.Render(pluginDirectory, fixture, 2, null), "outside this PDF");
Reject(() => LessonPdfRenderer.Render(pluginDirectory, fixture, 1, new(.9, .9, .5, .5)), "crop");
var damaged = Path.Combine(folder.Path, "damaged.pdf");
File.WriteAllText(damaged, "not a PDF");
Reject(() => LessonPdfRenderer.Render(pluginDirectory, damaged, 1, null), "could not be opened");
Console.WriteLine("PDF render checks passed: host COM isolation, Unicode paths, full page, crop, and invalid input.");

if (args.Length > 1)
{
    // Copy only the user's PDF, without the local sidecar or catalog.
    var local = Path.Combine(folder.Path, "book.pdf");
    File.Copy(args[1], local);
    var index = LessonTablatureIndex.LoadForPdf(local);
    Check(index?.Count == 365, "Built-in index recognized the supported PDF by hash");
    for (var exercise = 1; exercise <= 365; exercise++)
    {
        var entry = index!.Find(exercise)!;
        var image = LessonPdfRenderer.Render(pluginDirectory, local, entry.PdfPage, entry.Crop);
        var hasInk = false;
        for (var i = 0; i < image.Pixels.Length && !hasInk; i += 4)
            hasInk = image.Pixels[i + 1] < 128;
        Check(hasInk, $"Exercise {exercise} is not blank");
    }
    Console.WriteLine("Rendered all 365 indexed excerpts from the supported PDF with no sidecar.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CheckPixel(LessonPdfRenderer.Image image, double x, double y, byte b, byte g, byte r)
{
    var offset = ((int)(y * image.Height) * image.Width + (int)(x * image.Width)) * 4;
    Check(image.Pixels.AsSpan(offset, 4).SequenceEqual<byte>([b, g, r, 255]),
        $"Unexpected BGRA pixel at ({x}, {y}).");
}

static void Reject(Action render, string expectedMessage)
{
    try { render(); }
    catch (InvalidDataException e) when (e.Message.Contains(expectedMessage, StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException($"Expected PDF error containing '{expectedMessage}'.");
}

static void WriteTestPdf(string path)
{
    // Red in the top-left, blue in the bottom-right; PDF's origin is bottom-left.
    const string content = "1 0 0 rg 0 100 200 100 re f\n0 0 1 rg 200 0 200 100 re f\n";
    string[] objects =
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Resources << >> /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}endstream",
    ];
    using var output = File.Create(path);
    void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
    Write("%PDF-1.4\n");
    var offsets = new List<long>();
    foreach (var item in objects)
    {
        offsets.Add(output.Position);
        Write($"{offsets.Count} 0 obj\n{item}\nendobj\n");
    }
    var xref = output.Position;
    Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
    Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
}

sealed class HostMarshaller : ComWrappers
{
    protected override unsafe ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count) =>
        throw new NotSupportedException();
    protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) =>
        throw new NotSupportedException();
    protected override void ReleaseObjects(System.Collections.IEnumerable objects) =>
        throw new NotSupportedException();
}

sealed class ScratchDirectory : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("fftruebard-pdf-");
    public string Path => directory.FullName;
    public void Dispose() => directory.Delete(recursive: true);
}
