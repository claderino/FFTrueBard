using System.Text.RegularExpressions;

namespace FFTrueBard.Input;

public enum BindingKind { Ignore, Keyboard, Mouse }

public sealed record InputBinding(BindingKind Kind, string Key = "1", int MouseButton = 1,
    bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public string Canonical => Kind switch
    {
        BindingKind.Ignore => "",
        BindingKind.Mouse => $"Mouse{MouseButton}",
        _ => "Key:" + (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key,
    };
}

public sealed record MappingResolution(string Status, string Action = "")
{
    public string LogValue => Action.Length > 0 ? $"{Status}:{Action}" : Status;
}

/// <summary>Shared validation for the editor and log-only mapping route. No input injection.</summary>
public static class MappingRules
{
    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    // Rounded note labels that the current 70-1300 Hz detector can produce.
    public const int MinMidi = 37;
    public const int MaxMidi = 88;
    public static IReadOnlyList<string> Notes { get; } = Enumerable.Range(MinMidi, MaxMidi - MinMidi + 1)
        .Select(m => $"{NoteNames[m % 12]}{m / 12 - 1}").ToArray();
    public static IReadOnlyList<string> Keys { get; } = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
        .Concat(Enumerable.Range(0, 10).Select(n => n.ToString()))
        .Concat(Enumerable.Range(1, 24).Select(n => $"F{n}"))
        .Concat(["Space", "Tab", "Enter", "Escape", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
            "Left", "Right", "Up", "Down", "Shift", "Ctrl", "Alt", "LeftShift", "RightShift", "LeftCtrl", "RightCtrl",
            "LeftAlt", "RightAlt", "CapsLock", "NumLock", "ScrollLock", "PrintScreen", "Pause"])
        .Concat(Enumerable.Range(0, 10).Select(n => $"Numpad{n}"))
        .Concat(["NumpadAdd", "NumpadSubtract", "NumpadMultiply", "NumpadDivide", "NumpadDecimal", "Minus", "Equals",
            "LeftBracket", "RightBracket", "Backslash", "Semicolon", "Apostrophe", "Comma", "Period", "Slash", "Backtick"]).ToArray();

    public static bool TryNote(string? text, out string canonical, out int midi)
    {
        canonical = "";
        midi = -1;
        if (text is null || text.Length > 12)
            return false;
        var match = Regex.Match(text.Trim(), "^([A-Ga-g])([#b]?)([0-9])$");
        if (!match.Success)
            return false;
        var pitch = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11,
        };
        pitch += match.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 };
        midi = (match.Groups[3].Value[0] - '0' + 1) * 12 + pitch;
        if (midi < MinMidi || midi > MaxMidi)
            return false;
        canonical = $"{NoteNames[midi % 12]}{midi / 12 - 1}";
        return true;
    }

    public static bool TryBinding(string? text, out InputBinding binding)
    {
        binding = new(BindingKind.Ignore);
        if (text is null || text.Length > 100)
            return false;
        text = text.Trim();
        if (text.Length == 0)
            return true;
        if (text.Length == 6 && text.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase) && text[5] is >= '1' and <= '5')
        {
            binding = new(BindingKind.Mouse, MouseButton: text[5] - '0');
            return true;
        }
        if (!text.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = text[4..].Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0))
            return false;
        var keyText = parts[^1];
        keyText = keyText.ToUpperInvariant() switch
        {
            "ESC" => "Escape", "RETURN" => "Enter", "SPACEBAR" => "Space", "CONTROL" => "Ctrl", _ => keyText,
        };
        var key = Keys.FirstOrDefault(k => k.Equals(keyText, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts[..^1])
        {
            var modifier = part.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : part;
            if (!(modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
                modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase)) || !modifiers.Add(modifier))
                return false;
        }
        var ctrl = modifiers.Contains("Ctrl");
        var shift = modifiers.Contains("Shift");
        var alt = modifiers.Contains("Alt");
        if (ctrl && (key is "Ctrl" or "LeftCtrl" or "RightCtrl") ||
            shift && (key is "Shift" or "LeftShift" or "RightShift") ||
            alt && (key is "Alt" or "LeftAlt" or "RightAlt"))
            return false;
        binding = new(BindingKind.Keyboard, key, Ctrl: ctrl, Shift: shift, Alt: alt);
        return true;
    }

    public static bool TrySet(IDictionary<string, string> mappings, string? note, string? action,
        out string canonicalNote, out string error)
    {
        if (!TryNote(note, out canonicalNote, out _))
        {
            error = "Enter a supported note and octave, such as E2, F#3, or Bb3 (C#2-E6).";
            return false;
        }
        if (!TryBinding(action, out var binding))
        {
            error = "Choose Ignore, a supported keyboard key/modifier combination, or Mouse 1-5.";
            return false;
        }
        mappings[canonicalNote] = binding.Canonical;
        error = "";
        return true;
    }

    public static MappingResolution Resolve(IReadOnlyDictionary<string, string> mappings, string note, bool enabled, bool blocked)
    {
        if (blocked)
            return new("blocked:practice-tool");
        if (!enabled)
            return new("disabled");
        if (!TryNote(note, out var canonical, out _) || note != canonical)
            return new("unsupported-note");
        if (!mappings.TryGetValue(note, out var action))
            return new("unmapped");
        if (!TryBinding(action, out var binding))
            return new("invalid-mapping");
        return binding.Kind == BindingKind.Ignore ? new("ignored") : new("log-only", binding.Canonical);
    }
}
