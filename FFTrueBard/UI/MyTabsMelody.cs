using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private Task<PianoMelodyTab.Melody>? myTabMelodyJob;
    private CancellationTokenSource? myTabMelodyCancellation;
    private PianoMelodyTab.Melody? myTabMelody;
    private string myTabMelodyStatus = "";

    private void ResetMyTabMelody()
    {
        myTabMelodyCancellation?.Cancel();
        myTabMelodyCancellation?.Dispose();
        myTabMelodyCancellation = null;
        if (myTabMelodyJob is { } job)
            _ = job.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        myTabMelodyJob = null;
        myTabMelody = null;
        myTabMelodyStatus = "";
    }

    private void DrawMyTabMelody(TabPracticeFile tab)
    {
        if (myTabMelodyJob is { IsCompleted: true } job)
        {
            myTabMelodyJob = null;
            myTabMelodyCancellation?.Dispose();
            myTabMelodyCancellation = null;
            try
            {
                myTabMelody = job.GetAwaiter().GetResult();
                myTabMelodyStatus = "";
            }
            catch (Exception e) { myTabMelodyStatus = e.Message; }
        }
        if (!ImGui.CollapsingHeader("Piano melody to guitar TAB")) return;
        ImGui.TextWrapped("Read the treble melody from a supported piano PDF, without the bass or inner chord notes. " +
            "The preview uses standard tuning and no capo and stops at the first measure it can't read.");
        ImGui.BeginDisabled(myTabMelodyJob is not null);
        if (ImGui.SmallButton("Read piano melody"))
        {
            myTabMelodyStatus = "Reading the piano score locally...";
            myTabMelody = null;
            myTabMelodyCancellation = new();
            var cancellation = myTabMelodyCancellation.Token;
            myTabMelodyJob = Task.Run(() => PianoMelodyTab.ReadScore(page =>
                LessonPdfRenderer.Render(pluginDirectory, tab.Pdf, page, null, readTabDigits: true), cancellation));
        }
        ImGui.EndDisabled();
        if (myTabMelody is { } melody)
        {
            ImGui.TextWrapped($"Unreviewed melody draft: {(melody.StopReason is null ? "" : "first ")}{melody.Measures} measures · {melody.Bpm} BPM");
            if (melody.StopReason is { } reason) ImGui.TextWrapped($"Stopped at {reason}");
            ImGui.BeginDisabled(ReaderAudioLoading);
            if (ImGui.SmallButton("Play melody preview"))
            {
                try
                {
                    var score = TabPdfGuide.FromDocument(melody.Document);
                    myTabPage = 1;
                    myTabFollowScore = true;
                    StartTabDraftPreview(() => lessonPlayer.LoadTabGuide(DraftPreview, score), score, whole: true);
                }
                catch (Exception e) { myTabMelodyStatus = e.Message; }
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy melody TAB")) ImGui.SetClipboardText(melody.Text);
            if (ImGui.TreeNode("Show melody TAB"))
            {
                ImGui.TextUnformatted(melody.Text);
                ImGui.TreePop();
            }
        }
        if (myTabMelodyStatus.Length > 0) ImGui.TextWrapped(myTabMelodyStatus);
    }
}
