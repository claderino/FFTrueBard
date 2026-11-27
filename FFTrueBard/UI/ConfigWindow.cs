using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin.Services;
using FFTrueBard.Detection;
using FFTrueBard.Diagnostics;
using FFTrueBard.Practice;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow : IDisposable
{
    private readonly Configuration config;
    private readonly GuitarDetector detector;
    private readonly ChordPractice practice;
    private readonly DebugRecorder recorder;
    private readonly Action save;
    private readonly ITextureProvider? textures;
    private readonly IFontHandle? lessonHeading;
    private readonly string pluginDirectory;
    private readonly List<string> recentNotes = [];
    private DetectionFrame? latest;
    private bool visible;

    public ConfigWindow(Configuration config, GuitarDetector detector, ChordPractice practice,
        DebugRecorder recorder, Action save, ITextureProvider? textures = null, IFontHandle? lessonHeading = null,
        string? pluginDirectory = null)
    {
        this.config = config;
        config.Mapping ??= [];
        this.detector = detector;
        this.practice = practice;
        this.recorder = recorder;
        this.save = save;
        this.textures = textures;
        this.lessonHeading = lessonHeading;
        this.pluginDirectory = pluginDirectory ?? AppContext.BaseDirectory;
        history = new PracticeHistory(Path.Combine(Path.GetDirectoryName(recorder.DirectoryPath)!, "practice-history.json"));
        var configRoot = Path.GetDirectoryName(recorder.DirectoryPath)!;
        lessonProgress = new Lessons.LessonProgress(Path.Combine(configRoot, "lesson-progress.json"));
        lessonProgress.Load();
        InitializeLessons();
        backups = new SettingsBackups(Path.Combine(configRoot, "settings-backups"));
        exporter = new DiagnosticExport(configRoot);
        try { recoveryPoints = backups.List(); }
        catch (Exception e) { recoveryStatus = $"Cannot list recovery points: {e.Message}"; }
        ResetPractice();
        if (config.Mapping.TryGetValue(mappingNote, out var initialAction))
            LoadMapping(mappingNote, initialAction);
    }

    public void Open()
    {
        visible = true;
        detector.RefreshDevices();
    }

    public void Toggle()
    {
        if (visible)
        {
            visible = false;
            StopTools();
        }
        else
            Open();
    }

    public void Receive(DetectionFrame frame)
    {
        latest = frame;
        inputVisualizer.Observe(frame);
        ReceiveLessonPitch(frame);
        checklist.Observe(frame);
        ReceiveMappingNote(frame);
        tuner.Receive(frame, tunerString);
        calibration.Receive(frame);
        labelledTest.Receive(frame);
        var pastCountIn = !beats.Running || beats.BeatAt(frame.OnsetTimestampMs ?? frame.TimestampMs) >= 4;
        if (config.PracticeMode && !ToolsExclusive && (pastCountIn || frame.Released))
        {
            var checkedNote = !practice.Complete && !practice.WaitingForMute && frame.Decision == "accepted";
            var chord = practice.Chord.Name;
            var stringNumber = 6 - practice.StringIndex;
            var previousMisses = practice.Mistakes;
            var previousChecks = practice.CompletedChecks;
            practice.Receive(frame, Math.Clamp(config.PracticeCentsTolerance, 5, 50));
            if (checkedNote)
            {
                history.Observe(Chords.All[config.PracticeChord].Name,
                    config.AlternateChords && config.PracticeChord != config.TransitionChord ? Chords.All[config.TransitionChord].Name : "",
                    chord, stringNumber, practice.Mistakes == previousMisses, practice.CompletedChecks > previousChecks);
                beats.Observe(frame.OnsetTimestampMs ?? frame.TimestampMs, config.TimingOffsetMs);
            }
        }
        if (frame.Decision != "accepted")
            return;
        recentNotes.Insert(0, $"{DateTimeOffset.FromUnixTimeMilliseconds(frame.TimestampMs):HH:mm:ss.fff}  " +
            $"{frame.Note,-4} {frame.Frequency,8:0.00} Hz  {frame.Cents,+6:0.0;-0.0;0} ct  RMS {frame.Rms:0.0000}");
        if (recentNotes.Count > 100)
            recentNotes.RemoveAt(100);
    }

    private void ResetPractice()
    {
        config.PracticeChord = Math.Clamp(config.PracticeChord, 0, Chords.All.Length - 1);
        config.TransitionChord = Math.Clamp(config.TransitionChord, 0, Chords.All.Length - 1);
        practice.Configure(config.PracticeChord, config.TransitionChord, config.AlternateChords);
        history.NewSession();
    }

    public void Draw()
    {
        lessonDialogs.Draw();
        if (!visible)
            return;
        if (!ImGui.IsAnyItemActive())
            detectionEditBackedUp = routeEditBackedUp = false;
        var scale = ReaderScale;
        using var readerStyle = new ReaderStyle(scale);
        ImGui.SetNextWindowSize(new Vector2(770, 460) * scale, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("FFTrueBard  /  Practice###FFTrueBard - Practice & diagnostics", ref visible))
        {
            ImGui.End();
            if (!visible)
                StopTools();
            return;
        }

        if (ToolsExclusive)
        {
            ImGui.TextDisabled("Chord checks paused");
            ImGui.SameLine();
            if (ImGui.SmallButton("Resume chord checks"))
                StopTools();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stops active lesson audio, pitch drills, and other training tools.");
        }

        DrawWorkspaceNavigation();
        if (activeWorkspace != "My Tabs" && myTabToolActive) StopTools();
        switch (activeWorkspace)
        {
            case "Lessons":
                DrawLessons();
                break;
            case "My Tabs":
                DrawToolBody("My Tabs", DrawMyTabs);
                break;
            case "Practice":
                if (ImGui.BeginTabBar("practice_tools"))
                {
                    DrawToolTab("Chords", DrawPractice);
                    DrawToolTab("History", DrawHistory);
                    ImGui.EndTabBar();
                }
                break;
            case "Input":
                if (ImGui.BeginTabBar("input_tools"))
                {
                    DrawToolTab("Live", DrawInputDashboard);
                    DrawToolTab("Setup", DrawAudio);
                    DrawToolTab("Tuner", DrawTuner);
                    DrawToolTab("Calibration", DrawCalibration);
                    DrawToolTab("Detection test", DrawDetectionTest);
                    ImGui.EndTabBar();
                }
                break;
            case "Settings":
                if (ImGui.BeginTabBar("settings_tools"))
                {
                    DrawToolTab("Lesson files", DrawLessonFiles);
                    DrawToolTab("Mappings", DrawMappings);
                    DrawToolTab("Testing & recovery", DrawSupport);
                    DrawToolTab("Debug log", DrawDebug);
                    ImGui.EndTabBar();
                }
                break;
        }
        ImGui.End();
        if (!visible)
            StopTools();
    }

    private void DrawToolTab(string label, Action draw)
    {
        if (!BeginToolTab(label)) return;
        DrawToolBody(label, draw);
        ImGui.EndTabItem();
    }

    private static void DrawToolBody(string label, Action draw)
    {
        ImGui.Spacing();
        // Keep workspace tabs visible while a long tool page scrolls.
        if (ImGui.BeginChild($"body_{label}", Vector2.Zero)) draw();
        ImGui.EndChild();
    }

    private static bool PrimaryButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ReaderGold);
        ImGui.PushStyleColor(ImGuiCol.Button, ReaderBrass);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0x51412c));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0x665038));
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(4);
        return pressed;
    }

    private bool ChordPicker(string label, ref int index)
    {
        var changed = false;
        if (ImGui.BeginCombo(label, Chords.All[index].Name))
        {
            for (var i = 0; i < Chords.All.Length; i++)
            {
                if (ImGui.Selectable(Chords.All[i].Name, i == index))
                {
                    index = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void DrawPractice()
    {
        var first = config.PracticeChord;
        var second = config.TransitionChord;
        var alternate = config.AlternateChords;
        var changed = ChordPicker("First chord", ref first);
        changed |= ImGui.Checkbox("Alternate two chords", ref alternate);
        if (alternate)
            changed |= ChordPicker("Second chord", ref second);
        if (changed)
        {
            config.PracticeChord = first;
            config.TransitionChord = second;
            config.AlternateChords = alternate;
            ResetPractice();
            save();
        }
        if (alternate && first == second)
            ImGui.TextWrapped("Choose different chords to alternate. Currently repeating a single shape.");

        ImGui.Separator();
        ImGui.TextWrapped($"Shape: {practice.Chord.Name}  |  Pick one string at a time and mute between picks.");
        ImGui.TextWrapped(config.PracticeMode ? practice.Feedback : "Enable Practice mode to check notes.");

        if (ImGui.BeginTable("chord_shape", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            string[] headers = ["String", "Fret", "Finger", "Note", "Progress"];
            foreach (var header in headers)
                ImGui.TableSetupColumn(header);
            ImGui.TableHeadersRow();
            string[] strings = ["6: low E", "5: A", "4: D", "3: G", "2: B", "1: high e"];
            for (var i = 0; i < 6; i++)
            {
                var fret = practice.Chord.Frets[i];
                var progress = fret < 0 ? "Skip" :
                    practice.Complete || i < practice.StringIndex ? "Passed" :
                    i == practice.StringIndex ? "Play next" : "Waiting";
                ImGui.TableNextRow();
                string[] cells = [strings[i], fret < 0 ? "X" : fret.ToString(),
                    practice.Chord.Fingers[i], practice.Chord.NoteAt(i), progress];
                for (var col = 0; col < cells.Length; col++)
                {
                    ImGui.TableSetColumnIndex(col);
                    ImGui.TextUnformatted(cells[col]);
                }
            }
            ImGui.EndTable();
        }

        if (ToolsExclusive) ImGui.TextDisabled("Chord checks paused.");
        if (ImGui.Button("Retry current shape"))
            practice.Retry();
        ImGui.SameLine();
        if (ImGui.Button("Reset session"))
            ResetPractice();
        ImGui.TextWrapped($"{practice.CompletedChecks} completed  /  {practice.Mistakes} misses  /  Last check {practice.LastCheckSeconds:0.0} s");
        if (ImGui.CollapsingHeader("Rhythm & metronome") || beats.Running) DrawMetronome();
        if (ImGui.CollapsingHeader("Pitch options & live reading"))
        {
            var tolerance = config.PracticeCentsTolerance;
            if (ImGui.SliderFloat("Pitch tolerance (cents)", ref tolerance, 5, 50, "%.0f"))
            {
                config.PracticeCentsTolerance = tolerance;
                save();
            }
            ImGui.TextWrapped("Standard tuning, no capo. Finger numbers: 1 index, 2 middle, 3 ring, 4 little. " +
                "0 is open; skip X. The check measures pitch, not fingering, muting, or a full strum.");
            DrawLivePitch();
        }
    }

    private void DrawInputDashboard()
    {
        if (detector.IsCapturing)
            ImGui.TextWrapped($"{detector.ActiveDevice}  |  {detector.SampleRate:N0} Hz  |  Channel {detector.ActiveChannel}");
        else
            ImGui.TextWrapped(detector.StatusText);
        ImGui.Spacing();
        DrawInputVisualizer();
        if (!detector.IsCapturing && ImGui.Button("Open input setup"))
            requestedTab = "Setup";
        if (ImGui.CollapsingHeader("Capture health & drop counters")) DrawAudioHealth();
    }

    private void DrawAudio()
    {
        ImGui.TextWrapped(detector.StatusText);
        if (recoveryStatus.Length > 0) ImGui.TextWrapped(recoveryStatus);
        if (ImGui.BeginCombo("Input device", string.IsNullOrWhiteSpace(config.DeviceName) ? "Windows default" : config.DeviceName))
        {
            if (ImGui.Selectable("Windows default (may be a microphone)", config.DeviceName == ""))
            {
                if (ChangeSettings("Input device", () => config.DeviceName = ""))
                    RestartCapture();
            }
            foreach (var name in detector.DeviceNames)
            {
                if (ImGui.Selectable(name, name == config.DeviceName))
                {
                    if (ChangeSettings("Input device", () => config.DeviceName = name))
                        RestartCapture();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.Button("Refresh devices"))
            detector.RefreshDevices();
        ImGui.SameLine();
        if (ImGui.Button("Restart capture"))
            RestartCapture();
        var channel = config.InputChannel + 1;
        if (ImGui.SliderInt("Input channel (interface channel)", ref channel, 1, 8))
        {
            if (ChangeSettings("Input channel", () => config.InputChannel = channel - 1, createBackup: !routeEditBackedUp))
            {
                routeEditBackedUp = true;
                RestartCapture();
            }
        }
        ImGui.TextWrapped("Use the clean/dry guitar channel (NUX Dry Out). Pick the channel that carries your " +
            "guitar, not game playback. Capture uses the device's native sample rate.");
        if (ImGui.CollapsingHeader("Capture health & drop counters")) DrawAudioHealth();

        if (!ImGui.CollapsingHeader("Detection tuning")) return;

        var threshold = config.DetectionThreshold;
        var gate = config.RmsGate;
        var stable = config.StableFrames;
        var retrigger = config.RetriggerMs;
        var release = config.ReleaseMs;
        var changed = ImGui.SliderFloat("YIN threshold", ref threshold, 0.02f, 0.5f, "%.2f");
        changed |= ImGui.SliderFloat("RMS noise gate", ref gate, 0.0001f, 0.2f, "%.4f");
        changed |= ImGui.SliderInt("Stable frames", ref stable, 1, 10);
        changed |= ImGui.SliderInt("Minimum interval (ms)", ref retrigger, 0, 1000);
        changed |= ImGui.SliderInt("Quiet time to rearm (ms)", ref release, 40, 1000);
        if (changed)
        {
            if (ChangeSettings("Detection tuning", () =>
            {
                config.DetectionThreshold = threshold;
                config.RmsGate = gate;
                config.StableFrames = stable;
                config.RetriggerMs = retrigger;
                config.ReleaseMs = release;
            }, detection: true, createBackup: !detectionEditBackedUp))
                detectionEditBackedUp = true;
        }
        if (ImGui.Button("Reset detection defaults"))
        {
            recoveryOperation = "reset-detection";
            pendingRestore = null;
        }
        DrawRecoveryConfirmation();
        ImGui.TextWrapped("Raise the gate above your idle noise level, but below a soft pick. " +
            "Lower YIN thresholds are stricter. More stable frames reduce flicker but add delay. " +
            "A held note does not auto-repeat. A clear level dip and fresh pick can retrigger it after the minimum interval. " +
            "If a soft repeat is missed, mute for the quiet time. Lesson pitch drills still require muting between attempts.");
        DrawLivePitch();
    }

    private void RestartCapture()
    {
        recorder.Stop(); // Each recording belongs to one device/rate/channel.
        StopTools();
        calibration.Cancel(); // A suggestion measured on another device/channel is no longer valid.
        tuner.Reset();
        detector.Start();
        latest = null;
        inputVisualizer.Clear();
        practice.Retry();
        save();
    }

    private void DrawLivePitch()
    {
        ImGui.Separator();
        if (latest is null)
        {
            ImGui.TextUnformatted("Waiting for audio frames...");
            return;
        }
        ImGui.TextUnformatted($"Live: {(latest.Note.Length == 0 ? "--" : latest.Note)}   " +
            $"{latest.Frequency:0.00} Hz   {latest.Cents:+0.0;-0.0;0} cents   {latest.Decision}");
        ImGui.TextUnformatted($"RMS {latest.Rms:0.0000}   Peak {latest.Peak:0.000}   " +
            $"YIN confidence {latest.Confidence:0.000} (periodicity, not correctness)");
        if (latest.Peak >= 0.98f)
            ImGui.TextWrapped("Clipping warning: lower the guitar/interface input gain.");
    }

    private void DrawDebug()
    {
        var recording = recorder.Recording;
        if (ImGui.Checkbox("Record frequency diagnostics to JSONL", ref recording))
        {
            if (recording)
                recorder.Start();
            else
                recorder.Stop();
        }
        ImGui.TextWrapped(recorder.Status);
        ImGui.TextWrapped("Opt-in and local only. Records every processed frame, including rejected/sustained " +
            "notes, Hz, cents, RMS/peak, YIN confidence, decision, settings, practice target, and result. " +
            "No raw audio. Stops at 20 MiB; logs are not uploaded or automatically deleted.");
        ImGui.TextWrapped($"Folder: {recorder.DirectoryPath}");
        if (ImGui.Button("Copy log folder"))
            ImGui.SetClipboardText(recorder.DirectoryPath);
        if (!string.IsNullOrEmpty(recorder.LastPath))
        {
            ImGui.TextWrapped($"File: {recorder.LastPath}");
            if (ImGui.Button("Copy last log file path"))
                ImGui.SetClipboardText(recorder.LastPath);
        }
        ImGui.TextUnformatted($"Dropped log rows: {recorder.DroppedRows}   Dropped audio blocks: {detector.DroppedAudioBlocks}");
        ImGui.TextUnformatted($"Dropped detection frames: {detector.DroppedDetectionFrames}");
        DrawLivePitch();
        ImGui.Separator();
        ImGui.TextUnformatted("Accepted notes (newest first, last 100)");
        if (ImGui.Button("Clear note history"))
            recentNotes.Clear();
        ImGui.BeginChild("recent_notes", new Vector2(0, 180));
        foreach (var line in recentNotes)
            ImGui.TextUnformatted(line);
        ImGui.EndChild();
    }
}
