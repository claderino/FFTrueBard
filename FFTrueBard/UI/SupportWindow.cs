using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Diagnostics;
using FFTrueBard.Input;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private readonly ManualChecklist checklist = new();
    private readonly SettingsBackups backups;
    private readonly DiagnosticExport exporter;
    private IReadOnlyList<SettingsBackup> recoveryPoints = [];
    private SettingsBackup? pendingRestore;
    private string? recoveryOperation;
    private string recoveryStatus = "";
    private bool detectionEditBackedUp;
    private bool routeEditBackedUp;
    private string? requestedTab;
    private string checklistNotes = "";
    private string checklistStatus = "";
    private object? checklistMetadata;
    private IReadOnlyList<ExportSource> exportSources = [];
    private readonly HashSet<string> exportSelected = [];
    private ExportPreview? exportPreview;
    private Task<ExportPreview>? preparingExport;
    private Task<string>? writingExport;
    private bool reviewedExport;
    private bool includeDeviceNames;
    private int previewItem;
    private string exportStatus = "";
    private string archivePath = "";

    private bool BeginToolTab(string label)
    {
        var flags = requestedTab == label ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var selected = ImGui.BeginTabItem(label, flags);
        if (selected && requestedTab == label)
            requestedTab = null;
        return selected;
    }

    private void UpdateSupport()
    {
        if (preparingExport?.IsCompleted == true)
        {
            try
            {
                exportPreview = preparingExport.GetAwaiter().GetResult();
                reviewedExport = false;
                previewItem = 0;
                exportStatus = "Preview ready. Review the frozen contents before creating a local archive.";
            }
            catch (Exception e) { exportStatus = $"Cannot prepare export: {e.Message}"; }
            preparingExport = null;
        }
        if (writingExport?.IsCompleted == true)
        {
            try
            {
                archivePath = writingExport.GetAwaiter().GetResult();
                exportStatus = $"Saved local archive: {archivePath}. Nothing was uploaded.";
            }
            catch (Exception e) { exportStatus = $"Cannot create archive: {e.Message}"; }
            writingExport = null;
        }
    }

    private bool ChangeSettings(string reason, Action change, bool detection = false, bool createBackup = true)
    {
        var before = config.CaptureEditable();
        try
        {
            if (createBackup)
                backups.Create(reason, before);
        }
        catch (Exception e)
        {
            recoveryStatus = $"Edit blocked: could not save a recovery point. {e.Message}";
            return false;
        }
        try
        {
            change();
            if (detection)
                CancelMeasurements();
            detector.ApplySettings();
            save();
            recoveryStatus = $"Saved: {reason}. A pre-edit recovery point is available in Testing & recovery.";
            try { recoveryPoints = backups.List(); }
            catch (Exception e) { recoveryStatus += $" Could not refresh recovery list: {e.Message}"; }
            return true;
        }
        catch (Exception e)
        {
            config.RestoreEditable(before);
            detector.ApplySettings();
            recoveryStatus = $"Edit failed; previous in-memory settings restored: {e.Message}";
            return false;
        }
    }

    private void DrawAudioHealth()
    {
        var health = detector.Health;
        ImGui.TextUnformatted($"Audio health: {health.State}");
        ImGui.TextWrapped(health.State switch
        {
            "stopped" => "Capture/processing is stopped or failed. Check the status above and restart capture.",
            "starting" => "Waiting for the first capture and processing frames...",
            "no-audio-callbacks" or "capture-stalled" => "No recent audio callbacks. This is not evidence of a silent guitar; check the USB device/driver.",
            "processing-stalled" => "Audio callbacks arrived, but pitch processing has no recent frames. Restart capture and check drops.",
            "silent-input" => "Audio frames are arriving, but the selected channel is nearly zero. Check routing/cable/channel if you are playing.",
            "below-noise-gate" => "Audio frames are arriving below the gate. Normal while muted; lower the gate only if soft picks are being rejected.",
            "clipping" => "Input is near full scale. Lower interface/amp recording gain and repeat the test.",
            "invalid-samples" => "Input contains invalid numeric samples. Restart capture/check the driver.",
            _ => "Audio and processing frames are arriving with signal above the gate. This does not guarantee correct pitch recognition.",
        });
        ImGui.TextUnformatted($"Blocks: {health.Blocks}   Samples: {health.Samples}   Processed frames: {health.ProcessedFrames}   Accepted: {health.AcceptedNotes}");
        ImGui.TextUnformatted($"Last callback: {(health.LastBlockAgeMs is null ? "none" : health.LastBlockAgeMs + " ms ago")}   " +
            $"Last processed frame: {(health.LastFrameAgeMs is null ? "none" : health.LastFrameAgeMs + " ms ago")}");
        var db = 20 * Math.Log10(Math.Max(health.Rms, 0.0000001f));
        ImGui.ProgressBar((float)Math.Clamp((db + 80) / 80, 0, 1), new Vector2(-1, 18), $"Last RMS {db:0.0} dBFS / {health.Rms:0.00000}");
        ImGui.TextUnformatted($"Peak: {health.Peak:0.000}   Clipped frames this capture: {health.ClippedFrames}");
        ImGui.TextUnformatted($"Lifetime dropped audio blocks: {health.DroppedAudioBlocks}   Dropped delivery frames: {health.DroppedDetectionFrames}");
        ImGui.TextDisabled("Counts reset on capture restart, except lifetime drops. Health uses a 1.5 s stale threshold (2 s startup grace).");
    }

    private void SaveChecklist()
    {
        if (checklist.Id.Length == 0)
            return;
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(recorder.DirectoryPath)!, "training-reports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"manual-check-{checklist.Id}.json");
            var report = new { schemaVersion = 1, metadata = checklistMetadata, checklist, health = detector.Health };
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".new", path, true);
            checklistStatus = $"Saved checklist checkpoint: {path}";
        }
        catch (Exception e) { checklistStatus = $"Could not save checklist: {e.Message}"; }
    }

    private void PrepareChecklistStep()
    {
        StopTools();
        config.PracticeMode = true;
        switch (checklist.Index)
        {
            case 0:
                trainingPaused = true;
                requestedTab = "Audio & tuning";
                break;
            case 1:
                trainingPaused = tunerMode = true;
                requestedTab = "Tuner";
                break;
            case 2:
                testString = 0;
                testRepeats = 5;
                requestedTab = "Detection test";
                break;
            case 3:
                config.PracticeChord = 0;
                config.AlternateChords = false;
                ResetPractice();
                requestedTab = "Chord practice";
                break;
            case 4:
                config.PracticeMode = false;
                requestedTab = "Mappings";
                break;
        }
        save();
        checklistStatus = "Step prepared. Return to Testing & recovery to record your result and notes.";
    }

    private void DrawChecklist()
    {
        ImGui.TextWrapped("Manual test: silence -> six open strings -> repeated picks -> one chord -> mapping preview. " +
            "Results are your observations, not automatic hardware pass/fail claims. Checkpoints save only when you mark/advance/cancel a step.");
        ImGui.BeginDisabled(checklist.Running);
        if (ImGui.Button("Begin guided manual test"))
        {
            StopTools();
            config.PracticeMode = true;
            trainingPaused = true;
            save();
            checklist.Start(Now);
            checklistNotes = "";
            checklistMetadata = new { pluginVersion = typeof(FFTrueBardPlugin).Assembly.GetName().Version?.ToString(),
                device = detector.ActiveDevice, sampleRate = detector.SampleRate, channel = detector.ActiveChannel, settings = config.CaptureEditable() };
            SaveChecklist();
        }
        ImGui.EndDisabled();
        if (checklist.Id.Length == 0)
            return;
        ImGui.TextUnformatted($"{checklist.Outcome}: step {checklist.Index + 1}/{checklist.Steps.Count} - {checklist.Current.Name}");
        ImGui.TextWrapped(checklist.Current.Instructions);
        ImGui.BeginDisabled(!checklist.Running);
        if (ImGui.Button("Prepare step and open its tab"))
            PrepareChecklistStep();
        ImGui.InputTextMultiline("Step notes", ref checklistNotes, 2000, new Vector2(-1, 70));
        foreach (var result in new[] { "Pass", "Issue", "Skipped" })
        {
            if (ImGui.Button(result))
            {
                checklist.SetResult(result, checklistNotes);
                SaveChecklist();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.TextUnformatted($"Recorded result: {checklist.Current.Result}   Frames: {checklist.Current.Frames}   Clipped: {checklist.Current.ClippedFrames}");
        ImGui.BeginDisabled(checklist.Current.Result == "Not tested");
        if (ImGui.Button(checklist.Index + 1 == checklist.Steps.Count ? "Finish checklist" : "Next step"))
        {
            checklist.SetResult(checklist.Current.Result, checklistNotes);
            checklist.Next(Now);
            checklistNotes = checklist.Current.Notes;
            StopTools();
            config.PracticeMode = true;
            trainingPaused = checklist.Running;
            save();
            SaveChecklist();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel checklist"))
        {
            checklist.UpdateNotes(checklistNotes);
            checklist.Cancel(Now);
            StopTools();
            config.PracticeMode = true;
            save();
            SaveChecklist();
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(checklistStatus);
        foreach (var step in checklist.Steps)
            ImGui.TextUnformatted($"{step.Name}: {step.Result}");
    }

    private void DrawRecoveryConfirmation()
    {
        if (recoveryOperation is null)
            return;
        ImGui.Separator();
        ImGui.TextWrapped(recoveryOperation switch
        {
            "reset-mappings" => "Replace ALL saved mappings with the original defaults? Your current mappings will be backed up first.",
            "reset-detection" => "Reset threshold, noise gate, stability, interval, and release time? Device/channel and mappings stay unchanged. A backup is required first.",
            _ => $"Restore recovery point {pendingRestore?.CreatedUtc:u} ({pendingRestore?.Reason})? This restores mappings, input device/channel, and detection settings, not practice history. A current-state backup is created first.",
        });
        if (ImGui.Button("Confirm settings change"))
        {
            var operation = recoveryOperation;
            var target = pendingRestore;
            var before = config.CaptureEditable();
            var success = ChangeSettings($"Before {operation}", () =>
            {
                if (operation == "reset-mappings")
                    config.Mapping = MappingRules.DefaultMappings();
                else if (operation == "reset-detection")
                {
                    config.DetectionThreshold = 0.15f;
                    config.RmsGate = 0.004f;
                    config.StableFrames = 2;
                    config.RetriggerMs = 150;
                    config.ReleaseMs = 100;
                }
                else if (target is not null)
                    config.RestoreEditable(target.Settings);
                config.PracticeMode = true;
            }, detection: true);
            if (success)
            {
                StopTools();
                if (config.DeviceName != before.DeviceName || config.InputChannel != before.InputChannel)
                    RestartCapture();
                LoadMapping(mappingNote, config.Mapping.GetValueOrDefault(mappingNote, ""));
            }
            recoveryOperation = null;
            pendingRestore = null;
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep current settings"))
        {
            recoveryOperation = null;
            pendingRestore = null;
        }
    }

    private void DrawRecovery()
    {
        ImGui.TextWrapped("Pre-edit recovery points are saved before mapping edits, calibration Apply, tuning gestures, and resets. " +
            "The latest 20 valid points are retained locally. Restoring forces Practice mode on and never sends inputs.");
        if (ImGui.Button("Refresh recovery points"))
        {
            try { recoveryPoints = backups.List(); }
            catch (Exception e) { recoveryStatus = e.Message; }
        }
        ImGui.BeginDisabled(recoveryPoints.Count == 0);
        if (ImGui.Button("Undo latest edit (review newest backup)"))
        {
            pendingRestore = recoveryPoints[0];
            recoveryOperation = "restore";
        }
        ImGui.EndDisabled();
        ImGui.BeginChild("recovery_points", new Vector2(0, 130));
        foreach (var point in recoveryPoints)
            if (ImGui.Selectable($"{point.CreatedUtc:u} | {point.Reason}##{point.Id}", pendingRestore?.Id == point.Id))
            {
                pendingRestore = point;
                recoveryOperation = "restore";
            }
        ImGui.EndChild();
        DrawRecoveryConfirmation();
        ImGui.TextWrapped(recoveryStatus);
        ImGui.TextWrapped(backups.Warning);
        ImGui.TextWrapped($"Recovery folder: {backups.DirectoryPath}");
    }

    private void RefreshExportSources()
    {
        try
        {
            exportSources = exporter.ListSources();
            exportSelected.IntersectWith(exportSources.Select(s => s.RelativePath));
            exportPreview = null;
            reviewedExport = false;
        }
        catch (Exception e) { exportStatus = $"Cannot list files: {e.Message}"; }
    }

    private void DrawExport()
    {
        var busy = preparingExport is not null || writingExport is not null;
        ImGui.TextWrapped("Create a local ZIP with version/audio/settings snapshots and only the logs/reports you select. " +
            "No raw audio, credentials, automatic uploads, or automatic history/backups inclusion. Existing files may contain device names, paths, and your notes: inspect before sharing.");
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh available logs and reports"))
            RefreshExportSources();
        if (ImGui.Checkbox("Include device names in generated snapshots", ref includeDeviceNames))
        {
            exportPreview = null;
            reviewedExport = false;
        }
        ImGui.BeginChild("export_sources", new Vector2(0, 130));
        foreach (var source in exportSources)
        {
            var selected = exportSelected.Contains(source.RelativePath);
            if (ImGui.Checkbox($"{source.RelativePath} ({source.Bytes / 1024.0:0.0} KiB)", ref selected))
            {
                if (selected) exportSelected.Add(source.RelativePath); else exportSelected.Remove(source.RelativePath);
                exportPreview = null;
                reviewedExport = false;
            }
        }
        ImGui.EndChild();
        if (ImGui.Button("Prepare contents preview (stops debug recording)"))
        {
            recorder.Stop();
            var settings = config.CaptureEditable();
            if (!includeDeviceNames)
                settings = settings with { DeviceName = "[omitted]" };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var settingsJson = JsonSerializer.Serialize(settings, options);
            var sessionJson = JsonSerializer.Serialize(new { schemaVersion = 1, createdUtc = DateTime.UtcNow,
                pluginVersion = typeof(FFTrueBardPlugin).Assembly.GetName().Version?.ToString(), dalamudApiLevel = 15,
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                device = includeDeviceNames ? detector.ActiveDevice : "[omitted]", detector.SampleRate, detector.ActiveChannel, health = detector.Health }, options);
            var paths = exportSelected.ToArray();
            exportPreview = null;
            reviewedExport = false;
            preparingExport = Task.Run(() => exporter.Preview(paths, sessionJson, settingsJson));
        }
        ImGui.EndDisabled();
        if (busy)
            ImGui.TextUnformatted("Working on local files...");
        if (exportPreview is not null)
        {
            ImGui.TextUnformatted($"Frozen preview: {exportPreview.CreatedUtc:u} | {exportPreview.Items.Count} files | {exportPreview.TotalBytes / 1048576.0:0.00} MiB (limit 50 MiB)");
            if (ImGui.BeginCombo("Inspect preview file", exportPreview.Items[previewItem].Name))
            {
                for (var i = 0; i < exportPreview.Items.Count; i++)
                    if (ImGui.Selectable(exportPreview.Items[i].Name, i == previewItem))
                        previewItem = i;
                ImGui.EndCombo();
            }
            var item = exportPreview.Items[previewItem];
            ImGui.TextWrapped($"{item.Name}: {item.Content.Length} bytes; SHA-256 {item.Sha256}");
            ImGui.BeginChild("export_file_preview", new Vector2(0, 150));
            ImGui.TextUnformatted(Encoding.UTF8.GetString(item.Content, 0, Math.Min(item.Content.Length, 16384)));
            ImGui.EndChild();
            if (item.Content.Length > 16384)
                ImGui.TextWrapped("Only the first 16 KiB are displayed. Inspect the original file before approving a larger attachment.");
            ImGui.Checkbox("I reviewed these contents and want a local archive", ref reviewedExport);
            ImGui.BeginDisabled(!reviewedExport || busy);
            if (ImGui.Button("Create local ZIP from this preview"))
            {
                var frozen = exportPreview;
                writingExport = Task.Run(() => exporter.CreateArchive(frozen, reviewed: true));
            }
            ImGui.EndDisabled();
        }
        ImGui.TextWrapped(exportStatus);
        if (archivePath.Length > 0 && ImGui.Button("Copy ZIP path"))
            ImGui.SetClipboardText(archivePath);
    }

    private void DrawSupport()
    {
        DrawAudioHealth();
        if (ImGui.CollapsingHeader("Guided manual test", ImGuiTreeNodeFlags.DefaultOpen))
            DrawChecklist();
        if (ImGui.CollapsingHeader("Reset and undo"))
            DrawRecovery();
        if (ImGui.CollapsingHeader("Diagnostic export"))
            DrawExport();
    }
}
