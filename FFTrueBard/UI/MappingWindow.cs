using Dalamud.Bindings.ImGui;
using FFTrueBard.Detection;
using FFTrueBard.Input;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private readonly MappingNoteCapture mappingCapture = new();
    private DetectionFrame? lastMappingInput;
    private string mappingNote = "E2";
    private string mappingKey = "1";
    private BindingKind mappingKind = BindingKind.Keyboard;
    private int mappingMouse = 1;
    private bool mappingCtrl;
    private bool mappingShift;
    private bool mappingAlt;
    private string mappingFilter = "";
    private string mappingStatus = "";
    private bool mappingNeedsReplacement;
    private string? pendingRemoval;

    private InputBinding DraftBinding => new(mappingKind, mappingKey, mappingMouse, mappingCtrl, mappingShift, mappingAlt);

    private void ReceiveMappingNote(DetectionFrame frame)
    {
        var selected = mappingCapture.Receive(frame);
        if (selected is not null)
            mappingNote = selected;
        if (frame.Decision == "accepted")
            lastMappingInput = frame;
    }

    private void LoadMapping(string note, string? action)
    {
        mappingCapture.Reset();
        mappingNote = note;
        if (!MappingRules.TryBinding(action, out var binding))
        {
            mappingNeedsReplacement = true;
            mappingStatus = $"Stored action '{action}' is unsupported. Choose a replacement and Save; the original remains unchanged until then.";
            return;
        }
        mappingNeedsReplacement = false;
        mappingKind = binding.Kind;
        mappingKey = binding.Key;
        mappingMouse = binding.MouseButton;
        mappingCtrl = binding.Ctrl;
        mappingShift = binding.Shift;
        mappingAlt = binding.Alt;
        mappingStatus = $"Loaded {note}. Draft edits are not saved automatically.";
    }

    private void SaveMapping()
    {
        var original = config.Mapping;
        var updated = new Dictionary<string, string>(original);
        if (!MappingRules.TrySet(updated, mappingNote, DraftBinding.Canonical, out var note, out var error))
        {
            mappingStatus = error;
            return;
        }
        if (ChangeSettings($"Mapping {note}", () => config.Mapping = updated))
        {
            mappingNote = note;
            mappingStatus = $"Saved {note} -> {(updated[note].Length == 0 ? "Ignore" : updated[note])}. No keys or clicks were sent.";
        }
        else
            mappingStatus = recoveryStatus;
    }

    private void RemoveMapping(string note)
    {
        var original = config.Mapping;
        var updated = new Dictionary<string, string>(original);
        updated.Remove(note);
        if (ChangeSettings($"Remove mapping {note}", () => config.Mapping = updated))
        {
            mappingStatus = $"Removed {note}. Other bindings are unchanged.";
        }
        else
            mappingStatus = recoveryStatus;
        pendingRemoval = null;
    }

    private void DrawMappings()
    {
        ImGui.TextWrapped("Assign one detected note/octave to a keyboard key, modifier combination, or mouse button 1-5. " +
            "These are log-only bindings: this build does not send game inputs. Tap/hold behaviour is not implemented.");
        ImGui.TextWrapped("Audio identifies pitch, not string/fret position. The same note on two strings uses the same mapping. " +
            "Octaves are distinct: E2 and E4 can have different actions.");
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable mapping preview (when practice/tools are off)", ref enabled))
        {
            ChangeSettings("Mapping preview enabled", () => config.Enabled = enabled);
        }

        ImGui.Separator();
        if (ImGui.InputText("Note + octave", ref mappingNote, 16))
            mappingCapture.Reset();
        ImGui.SameLine();
        if (ImGui.BeginCombo("##supported_notes", "Choose note"))
        {
            foreach (var note in MappingRules.Notes)
                if (ImGui.Selectable(note, note == mappingNote))
                {
                    mappingNote = note;
                    mappingCapture.Reset();
                }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Examples: E2, F#3, Bb3. Flat names are saved as their sharp equivalent. Detector labels: C#2-E6.");

        ImGui.BeginDisabled(!detector.IsCapturing);
        if (ImGui.Button("Pick a note to select it"))
        {
            StopTools();
            trainingPaused = true;
            mappingCapture.Arm(Now);
            mappingStatus = "";
        }
        ImGui.EndDisabled();
        if (mappingCapture.Armed)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel note selection"))
                mappingCapture.Cancel();
        }
        ImGui.TextWrapped(mappingCapture.Status);

        if (ImGui.BeginCombo("Action type", mappingKind.ToString()))
        {
            foreach (var kind in Enum.GetValues<BindingKind>())
                if (ImGui.Selectable(kind.ToString(), mappingKind == kind))
                {
                    mappingKind = kind;
                    mappingCtrl = mappingShift = mappingAlt = false;
                    mappingNeedsReplacement = false;
                }
            ImGui.EndCombo();
        }
        if (mappingKind == BindingKind.Keyboard)
        {
            if (ImGui.BeginCombo("Keyboard key", mappingKey))
            {
                foreach (var key in MappingRules.Keys)
                    if (ImGui.Selectable(key, key == mappingKey))
                    {
                        mappingKey = key;
                        mappingNeedsReplacement = false;
                    }
                ImGui.EndCombo();
            }
            var changed = ImGui.Checkbox("Ctrl", ref mappingCtrl);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Shift", ref mappingShift);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Alt", ref mappingAlt);
            if (changed)
                mappingNeedsReplacement = false;
        }
        else if (mappingKind == BindingKind.Mouse)
        {
            if (ImGui.BeginCombo("Mouse button", $"Mouse {mappingMouse}"))
            {
                for (var i = 1; i <= 5; i++)
                    if (ImGui.Selectable($"Mouse {i}", mappingMouse == i))
                    {
                        mappingMouse = i;
                        mappingNeedsReplacement = false;
                    }
                ImGui.EndCombo();
            }
            ImGui.TextDisabled("1=left, 2=right, 3=middle, 4/5=side buttons. No mouse movement.");
        }

        var validNote = MappingRules.TryNote(mappingNote, out var canonicalNote, out _);
        var validBinding = MappingRules.TryBinding(DraftBinding.Canonical, out var binding);
        if (!validNote)
            ImGui.TextWrapped("Enter a valid note/octave in the detector's supported range before saving.");
        if (!validBinding)
            ImGui.TextWrapped("Do not select the same modifier as both the key and a modifier checkbox.");
        if (validNote && config.Mapping.TryGetValue(canonicalNote, out var previous))
            ImGui.TextWrapped($"{canonicalNote} already exists as '{(previous?.Length == 0 ? "Ignore" : previous)}'. Save replaces only this note's binding.");
        if (validNote && !string.Equals(mappingNote, canonicalNote, StringComparison.Ordinal))
            ImGui.TextUnformatted($"Will save under {canonicalNote}.");
        var action = validBinding ? binding.Canonical : DraftBinding.Canonical;
        ImGui.TextUnformatted($"Draft: {(validNote ? canonicalNote : mappingNote)} -> {(action.Length == 0 ? "Ignore" : action)}");
        if (validBinding && action.Length > 0)
        {
            var shared = config.Mapping.Where(pair => pair.Key != canonicalNote &&
                MappingRules.TryBinding(pair.Value, out var other) && other.Canonical == action).Select(pair => pair.Key).ToArray();
            if (shared.Length > 0)
                ImGui.TextWrapped($"Also assigned to {string.Join(", ", shared)}. Multiple notes may intentionally share an action.");
        }
        ImGui.BeginDisabled(!validNote || !validBinding || mappingCapture.Armed || mappingNeedsReplacement);
        if (ImGui.Button("Save binding"))
            SaveMapping();
        ImGui.EndDisabled();
        ImGui.TextWrapped(mappingStatus);
        ImGui.TextDisabled("Changing the note adds/updates that note; it does not rename or remove the original row.");

        ImGui.Separator();
        ImGui.InputText("Filter saved mappings", ref mappingFilter, 100);
        if (ImGui.BeginTable("saved_mappings", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Note");
            ImGui.TableSetupColumn("Saved action");
            ImGui.TableSetupColumn("Edit / remove");
            ImGui.TableHeadersRow();
            foreach (var pair in config.Mapping.Where(p => p.Key.Contains(mappingFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.Value?.Contains(mappingFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(p => MappingRules.TryNote(p.Key, out _, out var midi) ? midi : int.MaxValue).ThenBy(p => p.Key).ToArray())
            {
                ImGui.PushID(pair.Key);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(pair.Key);
                ImGui.TableSetColumnIndex(1);
                var supported = MappingRules.TryBinding(pair.Value, out var saved);
                var canonicalLabel = MappingRules.TryNote(pair.Key, out var normalized, out _) && normalized == pair.Key;
                ImGui.TextUnformatted(supported ? saved.Kind == BindingKind.Ignore ? "Ignore" : saved.Canonical : $"Unsupported: {pair.Value}");
                if (!canonicalLabel)
                    ImGui.TextWrapped("Noncanonical/unsupported note label: add a canonical binding, then remove this row if desired.");
                ImGui.TableSetColumnIndex(2);
                if (ImGui.SmallButton("Edit"))
                    LoadMapping(pair.Key, pair.Value);
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                    pendingRemoval = pair.Key;
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (pendingRemoval is not null)
        {
            ImGui.TextUnformatted($"Remove saved mapping for {pendingRemoval}?");
            if (ImGui.Button("Confirm removal"))
                RemoveMapping(pendingRemoval);
            ImGui.SameLine();
            if (ImGui.Button("Keep mapping"))
                pendingRemoval = null;
        }
        if (ImGui.Button("Reset mappings to original defaults..."))
        {
            pendingRestore = null;
            recoveryOperation = "reset-mappings";
        }
        DrawRecoveryConfirmation();

        ImGui.Separator();
        if (lastMappingInput is null)
            ImGui.TextUnformatted("No accepted note yet. Play an isolated note to inspect its mapping.");
        else
        {
            var result = MappingRules.Resolve(config.Mapping, lastMappingInput.Note, config.Enabled, config.PracticeMode || ToolsExclusive);
            ImGui.TextWrapped($"Last accepted note: {lastMappingInput.Note}, {lastMappingInput.Frequency:0.00} Hz, " +
                $"{DateTimeOffset.FromUnixTimeMilliseconds(lastMappingInput.TimestampMs):HH:mm:ss.fff}. Current mapping: {result.LogValue}");
            ImGui.TextDisabled("This preview reflects saved bindings and current mode; it does not re-send the note.");
        }
    }
}
