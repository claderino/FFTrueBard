using Dalamud.Bindings.ImGui;
using FFTrueBard.Lessons;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private Task<PianoMelodyTab.Melody>? myTabMelodyJob;
    private PianoMelodyTab.Melody? myTabMelody;
    private string myTabMelodyStatus = "";

    private void ResetMyTabMelody()
    {
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
            try
            {
                myTabMelody = job.GetAwaiter().GetResult();
                myTabMelodyStatus = "";
            }
            catch (Exception e) { myTabMelodyStatus = e.Message; }
        }
        if (!ImGui.CollapsingHeader("Piano melody to guitar TAB")) return;
        ImGui.TextWrapped("Read the first system of a supported piano PDF. Keep the top melody, not the bass or chords. " +
            "The preview uses standard tuning and no capo.");
        ImGui.BeginDisabled(myTabMelodyJob is not null);
        if (ImGui.SmallButton("Read piano opening"))
        {
            myTabMelodyStatus = "Reading the piano opening locally...";
            myTabMelody = null;
            myTabMelodyJob = Task.Run(() => PianoMelodyTab.ReadOpening(
                LessonPdfRenderer.Render(pluginDirectory, tab.Pdf, 1, null, readTabDigits: true)));
        }
        ImGui.EndDisabled();
        if (myTabMelody is { } melody)
        {
            ImGui.TextWrapped($"Unreviewed melody draft: first {melody.Measures} measures · {melody.Bpm} BPM");
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
