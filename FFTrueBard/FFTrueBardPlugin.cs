using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFTrueBard.Detection;
using FFTrueBard.Diagnostics;
using FFTrueBard.Input;
using FFTrueBard.Practice;
using FFTrueBard.UI;

namespace FFTrueBard;

public sealed class FFTrueBardPlugin : IDalamudPlugin
{
    public const string CommandName = "/ftruebard";

    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;

    private readonly Configuration config;
    private readonly GuitarDetector detector;
    private readonly IInputInjector inputInjector;
    private readonly ConfigWindow configWindow;
    private readonly ChordPractice practice;
    private readonly DebugRecorder recorder;

    public FFTrueBardPlugin()
    {
        config = (Configuration?)PluginInterface.GetPluginConfig() ?? new Configuration();
        detector = new GuitarDetector(config, Log);
        practice = new ChordPractice();
        recorder = new DebugRecorder(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "diagnostics"));
        configWindow = new ConfigWindow(config, detector, practice, recorder,
            () => PluginInterface.SavePluginConfig(config));

        // Swap in a real injector implementation when ready (see InputInjector.cs).
        inputInjector = new DummyInputInjector(Log);

        detector.Start();

        PluginInterface.UiBuilder.Draw += configWindow.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Open;
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FFTrueBard configuration window.",
        });

        Log.Info("FFTrueBard loaded (built-in detector).");
    }

    private void OnCommand(string command, string args) => configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        while (detector.Frames.TryRead(out var frame))
        {
            configWindow.Receive(frame);
            var expected = config.PracticeMode ? practice.Expected : "";
            var action = config.PracticeMode ? "blocked:practice" : "disabled";
            if (config.PracticeMode)
                practice.Receive(frame, Math.Clamp(config.PracticeCentsTolerance, 5, 50));
            else if (config.Enabled && frame.Decision == "accepted" &&
                config.Mapping.TryGetValue(frame.Note, out var mapping) && !string.IsNullOrEmpty(mapping))
            {
                inputInjector.Dispatch(mapping, isDown: true);
                action = $"log-only:{mapping}";
            }

            recorder.Record(frame, detector.ActiveDevice, detector.SampleRate, detector.ActiveChannel,
                expected, config.PracticeMode ? practice.Feedback : "", action, detector.DroppedAudioBlocks,
                config.PracticeCentsTolerance, detector.DroppedDetectionFrames);
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= configWindow.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Open;
        detector.Dispose();
        recorder.Dispose();
        PluginInterface.SavePluginConfig(config);
    }
}
