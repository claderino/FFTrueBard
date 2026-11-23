using System.Security.Cryptography;
using System.Text.Json;
using FFTrueBard.Lessons;

internal static class LessonTablatureIndexTests
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fftruebard-tablature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var pdf = Path.Combine(directory, "my-book.pdf");
        var sidecar = Path.ChangeExtension(pdf, ".tablature.json");
        try
        {
            var bytes = "%PDF-1.4\n% fingerprint test\n"u8.ToArray();
            File.WriteAllBytes(pdf, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            check(LessonTablatureIndex.LoadForPdf(pdf) is null, "Tablature index is optional");
            var first = new LessonTablatureEntry(1, 4, new(.1, .3, .8, .18));
            var last = new LessonTablatureEntry(365, 112, new(.1, .5, .8, .28));
            Write(hash, [first, last]);
            var library = LessonLibrary.Scan(new("", pdf, "", ""));
            check(library.Tablature is { Count: 2 } index && index.Find(1) == first &&
                index.Find(365) == last && index.Find(2) is null, "Book excerpts are indexed independently of guided cards");

            foreach (var (digest, entries) in new (string, LessonTablatureEntry[])[]
            {
                (new string('0', 64), [first]),
                (hash, [first, first]),
                (hash, [first with { PdfPage = 0 }]),
                (hash, [first with { Exercise = 366 }]),
                (hash, [first with { Crop = new(.3, .5, .8, .2) }]),
            })
            {
                Write(digest, entries);
                library = LessonLibrary.Scan(new("", pdf, "", ""));
                check(library.Tablature is null && library.Paths.Pdf == pdf &&
                    library.Warnings.Any(w => w.StartsWith("Tablature index ignored", StringComparison.Ordinal)),
                    "Bad index is rejected without blocking the local book");
            }
            Write(hash, [first]);
            File.AppendAllText(pdf, "changed edition");
            check(LessonLibrary.Scan(new("", pdf, "", "")).Tablature is null, "Changing PDF bytes invalidates the saved index");

            void Write(string digest, LessonTablatureEntry[] exercises) =>
                File.WriteAllText(sidecar, JsonSerializer.Serialize(new { schemaVersion = 1, pdfSha256 = digest, exercises }));
        }
        finally
        {
            File.Delete(sidecar);
            File.Delete(pdf);
            Directory.Delete(directory);
        }
    }
}
