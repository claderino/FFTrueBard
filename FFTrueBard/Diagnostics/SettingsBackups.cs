using System.Text.Json;
using System.Text;

namespace FFTrueBard.Diagnostics;

public sealed record EditableSettings(string DeviceName, int InputChannel, float Threshold, float Gate,
    int StableFrames, int RetriggerMs, int ReleaseMs, bool MappingEnabled, Dictionary<string, string> Mapping)
{
    public EditableSettings Copy() => this with { Mapping = new Dictionary<string, string>(Mapping) };
    public bool IsValid => DeviceName is not null && InputChannel is >= 0 and <= 7 &&
        float.IsFinite(Threshold) && Threshold is >= 0.02f and <= 0.5f && float.IsFinite(Gate) && Gate is >= 0.0001f and <= 0.2f &&
        StableFrames is >= 1 and <= 10 && RetriggerMs is >= 0 and <= 1000 && ReleaseMs is >= 40 and <= 1000 && Mapping is not null;
}

public sealed record SettingsBackup(int SchemaVersion, string Id, DateTime CreatedUtc, string Reason, EditableSettings Settings);

/// <summary>Recovery points contain only editable mapping/capture settings, never practice history.</summary>
public sealed class SettingsBackups(string directory)
{
    public const int Retention = 20;
    public const int MaxFileBytes = 1024 * 1024;
    public string DirectoryPath => directory;
    public string Warning { get; private set; } = "";

    public SettingsBackup Create(string reason, EditableSettings settings)
    {
        if (!settings.IsValid)
            throw new InvalidDataException("Current settings are invalid; no safe recovery point could be created.");
        Directory.CreateDirectory(directory);
        var existing = List();
        var now = DateTime.UtcNow;
        if (existing.Count > 0 && now <= existing[0].CreatedUtc)
            now = existing[0].CreatedUtc.AddTicks(1);
        var backup = new SettingsBackup(1, Guid.NewGuid().ToString("N"), now, reason, settings.Copy());
        var path = Path.Combine(directory, $"settings-{backup.Id}.json");
        var content = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            throw new InvalidDataException("Settings recovery point exceeds the supported 1 MiB size; no edit was applied.");
        File.WriteAllText(path + ".new", content);
        File.Move(path + ".new", path);
        foreach (var old in List().Skip(Retention))
        {
            try { File.Delete(Path.Combine(directory, $"settings-{old.Id}.json")); }
            catch (Exception e) { Warning = $"Recovery point saved, but retention cleanup failed: {e.Message}"; }
        }
        return backup;
    }

    public IReadOnlyList<SettingsBackup> List()
    {
        Warning = "";
        if (!Directory.Exists(directory))
            return [];
        var entries = new List<SettingsBackup>();
        foreach (var path in Directory.EnumerateFiles(directory, "settings-*.json"))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > MaxFileBytes)
                    continue;
                var entry = JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(path));
                if (entry is null || entry.SchemaVersion != 1 || !Guid.TryParseExact(entry.Id, "N", out _) ||
                    Path.GetFileName(path) != $"settings-{entry.Id}.json" || entry.Settings is null || !entry.Settings.IsValid)
                    throw new InvalidDataException("Unsupported recovery point.");
                entries.Add(entry);
            }
            catch (Exception e) { Warning = $"An unreadable recovery point was skipped, not overwritten: {e.Message}"; }
        }
        return entries.OrderByDescending(e => e.CreatedUtc).ThenByDescending(e => e.Id).ToArray();
    }
}
