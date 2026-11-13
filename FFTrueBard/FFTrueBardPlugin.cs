using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFTrueBard.Input;
using FFTrueBard.Link;
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
    private readonly NoteLinkServer linkServer;
    private readonly IInputInjector inputInjector;
    private readonly ConfigWindow configWindow;

    public FFTrueBardPlugin()
    {
        config = (Configuration?)PluginInterface.GetPluginConfig() ?? new Configuration();
        linkServer = new NoteLinkServer(config.ListenPort, Log);
        configWindow = new ConfigWindow(config, linkServer);

        // Swap in a real injector implementation when ready (see InputInjector.cs).
        inputInjector = new DummyInputInjector(Log);

        linkServer.Start();

        PluginInterface.UiBuilder.Draw += configWindow.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Open;
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FFTrueBard configuration window.",
        });

        Log.Info("FFTrueBard loaded. Waiting for the note detector to connect...");
    }

    private void OnCommand(string command, string args) => configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        while (linkServer.Events.TryRead(out var ev))
        {
            configWindow.PushEvent(ev.Note, ev.Amp);

            if (!config.Enabled)
                continue;

            if (config.Mapping.TryGetValue(ev.Note, out var action) && !string.IsNullOrEmpty(action))
                inputInjector.Dispatch(action, isDown: true);
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= configWindow.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Open;
        linkServer.Dispose();
        PluginInterface.SavePluginConfig(config);
    }
}
