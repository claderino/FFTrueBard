using Dalamud.Plugin.Services;

namespace FFTrueBard.Input;

/// <summary>
/// Turns a mapped action string (from <see cref="Configuration.Mapping"/>) into in-game input.
/// </summary>
public interface IInputInjector
{
    /// <param name="action">
    /// "Mouse1".."Mouse5" for mouse buttons, "Key:X" for a keyboard key (e.g. "Key:Space", "Key:1").
    /// </param>
    /// <param name="isDown">true = press/hold-down, false = release.</param>
    void Dispatch(string action, bool isDown);
}

/// <summary>
/// Logs instead of injecting. This is the current default so the pipeline
/// (detector -> plugin -> UI) can be verified end-to-end with zero risk.
///
/// TODO (real injection) - three proven options, in rough order of preference:
///  1. Write directly to the game's input state via FFXIVClientStructs the way
///     input-automation plugins (AutoDuty / TextAdvance / ECommons automation) do.
///     Most reliable: works windowless, no synthetic-input quirks.
///  2. Dalamud's IGamepadState service: map notes to virtual gamepad buttons,
///     which gives clean "held" semantics for movement keys.
///  3. SendInput scancodes from inside the game process (same-process SendInput
///     is generally accepted; use scancodes, not virtual keys).
/// Pick one, implement it in a class implementing IInputInjector, and swap the
/// construction in FFTrueBardPlugin.
/// </summary>
public sealed class DummyInputInjector(IPluginLog log) : IInputInjector
{
    public void Dispatch(string action, bool isDown)
        => log.Debug($"FFTrueBard [dry-run] {(isDown ? "press  " : "release")} {action}");
}
