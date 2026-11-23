using FFTrueBard.Detection;
using FFTrueBard.UI;

internal static class VisualizerTests
{
    public static void Run(Action<bool, string> check)
    {
        var settings = new DetectionSettings(0.15f, 0.004f, 2, 150, 100);
        var note = new DetectionFrame(1_000, 329.63, "E4", 64, 0, 0.08f, 0.3f, 0.94f,
            "accepted", false, settings, FreshAttack: true);
        var history = new InputVisualizerHistory();
        var output = new DetectionFrame[InputVisualizerHistory.Capacity];
        check(history.CopyRecent(output, 1_000) == 0, "Empty scope does not show any frame");
        for (var i = 0; i < InputVisualizerHistory.Capacity + 15; i++)
            history.Observe(note with { TimestampMs = 1_000 + 42 * i });
        var last = 1_000 + 42 * (InputVisualizerHistory.Capacity + 14);
        check(history.CopyRecent(output, last) == InputVisualizerHistory.Capacity &&
            output[0].TimestampMs == 1_000 + 42 * 15 && output[^1].TimestampMs == last,
            "Scope retains only its bounded recent history in delivery order");
        check(history.CopyRecent(output, last + 501) == 0 && history.CopyRecent(output, last - 101) == 0,
            "Scope hides stale and future-clock frames");
        history.Observe(note with { TimestampMs = last + 600 });
        check(history.CopyRecent(output, last + 600) == 1, "Capture gap does not connect two traces");
        history.Observe(note with { TimestampMs = last + 600, Decision = "sustained" });
        check(history.CopyRecent(output, last + 600) == 2 && output[1].Decision == "sustained",
            "Frames delivered in the same timestamp batch are still drawn separately");
        history.Observe(note with { TimestampMs = 999 });
        check(history.CopyRecent(output, 999) == 1, "Backwards clock starts a fresh trace");
        history.Clear();
        check(history.CopyRecent(output, 999) == 0, "Capture route change can clear visual history immediately");

        check(InputVisualizerHistory.LevelFraction(0) == 0 && InputVisualizerHistory.LevelFraction(1) == 1 &&
            InputVisualizerHistory.LevelFraction(0.01f) == 0.5f, "RMS axis uses dBFS and a bounded display range");
        check(InputVisualizerHistory.LevelFraction(float.NaN) is null, "Invalid levels create no plot point");
        check(InputVisualizerHistory.PitchFraction(note) == 24 / 47f &&
            InputVisualizerHistory.PitchFraction(note with { Cents = 20 }) > 24 / 47f,
            "Pitch axis uses sounding MIDI and cents");
        check(new[] { note with { Peak = 0.99f }, note with { Decision = "gated" }, note with { Decision = "unvoiced" },
            note with { Midi = 88 }, note with { Cents = double.NaN } }.All(f => InputVisualizerHistory.PitchFraction(f) is null),
            "Clipped, gated, unvoiced, out-of-range, and invalid pitches do not appear as healthy trace points");

        check(LessonVisualizerPage.PageAt(7, 32, 8) == 0 && LessonVisualizerPage.PageAt(8, 32, 8) == 1,
            "Note visualization follows the active target at page boundaries");
        check(LessonVisualizerPage.PageAt(32, 32, 8) == 3 && LessonVisualizerPage.PageAt(1, 1, 1) == 0,
            "Finished and single-target sequences select valid pages");
    }
}
