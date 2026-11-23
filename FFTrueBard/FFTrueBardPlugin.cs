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
    [PluginService] public static ITextureProvider Textures { get; private set; } = null!;

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
            () => PluginInterface.SavePluginConfig(config), Textures);

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
            var expected = configWindow.ExpectedContext;
            configWindow.Receive(frame);
            var action = "not-accepted";
            if (frame.Decision == "accepted")
            {
                var resolved = MappingRules.Resolve(config.Mapping, frame.Note, config.Enabled,
                    config.PracticeMode || configWindow.ToolsExclusive);
                if (resolved.Status == "log-only")
                    inputInjector.Dispatch(resolved.Action, isDown: true);
                action = resolved.LogValue;
            }

            recorder.Record(frame, detector.ActiveDevice, detector.SampleRate, detector.ActiveChannel,
                expected, configWindow.FeedbackContext, action, detector.DroppedAudioBlocks,
                config.PracticeCentsTolerance, detector.DroppedDetectionFrames);
        }
        configWindow.Update();
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= configWindow.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Open;
        detector.Dispose();
        configWindow.Dispose();
        recorder.Dispose();
        PluginInterface.SavePluginConfig(config);
    }
}
