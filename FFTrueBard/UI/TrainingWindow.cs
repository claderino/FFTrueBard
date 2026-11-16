using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFTrueBard.Practice;

namespace FFTrueBard.UI;

public sealed partial class ConfigWindow
{
    private readonly GuitarTuner tuner = new();
    private readonly CalibrationWizard calibration = new();
    private readonly LabelledTest labelledTest = new();
    private readonly BeatClock beats = new();
    private readonly MetronomePlayer clickPlayer = new();
    private readonly PracticeHistory history;
    private int tunerString = -1;
    private int testString;
    private int testRepeats = 5;
    private bool tunerMode;
    private bool trainingPaused;
    private bool testSaved;
    private string reportStatus = "";
    private object? testMetadata;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool ToolsExclusive => trainingPaused || tunerMode || calibration.Running || labelledTest.Running;
    public string ExpectedContext => mappingCapture.Armed ? "mapping-note-selection" : calibration.Running ? $"calibration:{calibration.Phase}" :
        labelledTest.Running ? $"labelled-test:{labelledTest.Expected}:{labelledTest.Phase}" :
        tunerMode ? "tuner" : trainingPaused ? "practice-paused" : config.PracticeMode ? practice.Expected : "";
    public string FeedbackContext => mappingCapture.Armed ? mappingCapture.Status : calibration.Running ? calibration.Status :
        labelledTest.Running ? $"{labelledTest.Phase}; trial {labelledTest.TrialNumber}/{labelledTest.TotalTrials}" :
        tunerMode ? "Tuning; chord grading paused." : trainingPaused ? "Chord grading paused after training tool." :
        config.PracticeMode ? practice.Feedback : "";

    public void Update()
    {
        mappingCapture.Tick(Now);
        calibration.Tick(Now);
        labelledTest.Tick(Now);
        if (labelledTest.Phase == "complete" && !testSaved)
        {
            testSaved = true;
            try
            {
                var directory = Path.Combine(Path.GetDirectoryName(recorder.DirectoryPath)!, "training-reports");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"detection-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                var trials = labelledTest.Trials;
                var report = new { schemaVersion = 1, completedUtc = DateTime.UtcNow, metadata = testMetadata,
                    trials, noAcceptedDetection = trials.Count(t => t.Accepted.Length == 0),
                    noCorrectDetection = trials.Count(t => t.Correct == 0),
                    octaveErrors = trials.Sum(t => t.OctaveErrors), wrongNotes = trials.Sum(t => t.WrongNotes),
                    extraDetections = trials.Sum(t => t.ExtraDetections),
                    droppedAudioBlocks = detector.DroppedAudioBlocks, droppedFrames = detector.DroppedDetectionFrames };
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                reportStatus = $"Saved report: {path}";
            }
            catch (Exception e)
            {
                reportStatus = $"Could not save the report: {e.Message}";
            }
        }
    }

    private void CancelMeasurements()
    {
        if (calibration.Running)
            calibration.Cancel();
        if (labelledTest.Running)
        {
            labelledTest.Cancel();
            reportStatus = "Test cancelled; no completed report saved.";
        }
        practice.Retry();
    }

    private void StopMetronome()
    {
        beats.Stop();
        clickPlayer.Stop();
    }

    private void StopTools()
    {
        mappingCapture.Cancel();
        CancelMeasurements();
        tunerMode = false;
        trainingPaused = false;
        StopMetronome();
    }

    private void EnterTool()
    {
        StopTools();
        config.PracticeMode = true;
        trainingPaused = true;
        history.NewSession();
        save();
    }

    public void Dispose() => StopTools();

    private void DrawTuner()
    {
        var active = tunerMode;
        if (ImGui.Checkbox("Pause chord checks while tuning", ref active))
        {
            EnterTool();
            tunerMode = active;
            trainingPaused = active;
        }
        if (ImGui.BeginCombo("Tuner target", tunerString < 0 ? "Auto: nearest open string" : OpenStrings.Names[tunerString]))
        {
            if (ImGui.Selectable("Auto: nearest open string", tunerString < 0))
            {
                tunerString = -1;
                tuner.Reset();
            }
            for (var i = 0; i < 6; i++)
            {
                if (ImGui.Selectable($"String {6 - i}: {OpenStrings.Names[i]}", tunerString == i))
                {
                    tunerString = i;
                    tuner.Reset();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Standard tuning, A4 = 440 Hz. Pick one open string, let the attack settle, and adjust gently. " +
            "Auto matches within 150 cents; select a string manually if further out of tune.");
        var reading = tuner.Reading;
        if (reading is null || Now - reading.TimestampMs > 500)
        {
            ImGui.TextUnformatted("Waiting for a clear single note...");
            return;
        }
        var inTune = reading.Samples >= 3 && Math.Abs(reading.Cents) <= 5;
        ImGui.TextUnformatted($"{reading.Target}   {reading.Frequency:0.00} Hz   {reading.Cents:+0.0;-0.0;0} cents");
        ImGui.TextUnformatted(inTune ? "In tune (within 5 cents)" : reading.Cents < 0 ? "Flat: raise pitch gently" : "Sharp: lower pitch gently");
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - 10);
        var draw = ImGui.GetWindowDrawList();
        var rulerColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var y = origin.Y + 30;
        draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + width, y), rulerColor, 2);
        for (var i = 0; i <= 10; i++)
        {
            var x = origin.X + width * i / 10;
            draw.AddLine(new Vector2(x, y - 7), new Vector2(x, y + 7), rulerColor, 1);
        }
        var needle = origin.X + width * (float)(Math.Clamp(reading.Cents, -50, 50) + 50) / 100;
        draw.AddLine(new Vector2(needle, y - 22), new Vector2(needle, y + 22),
            ImGui.GetColorU32(inTune ? ImGuiCol.CheckMark : ImGuiCol.PlotHistogram), 4);
        ImGui.Dummy(new Vector2(width, 65));
        ImGui.TextUnformatted("-50 cents (flat)                           0                           +50 cents (sharp)");
        ImGui.TextDisabled("Needle uses a five-frame median; displayed Hz is the newest estimate.");
    }

    private void DrawCalibration()
    {
        ImGui.TextWrapped("Measure silence, soft low-E picks, then normal low-E picks. Each recording lasts five seconds. " +
            "Use your dry input; do not change guitar volume or interface gain midway.");
        ImGui.BeginDisabled(detector.SampleRate <= 0);
        if (ImGui.Button("Start / restart calibration"))
        {
            EnterTool();
            calibration.Start(Now);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel calibration"))
            calibration.Cancel();
        if (calibration.Phase == "ready-soft" && ImGui.Button("Record soft E2 picks"))
            calibration.Begin("soft", Now);
        if (calibration.Phase == "ready-normal" && ImGui.Button("Record normal E2 picks"))
            calibration.Begin("normal", Now);
        ImGui.TextWrapped(calibration.Status);
        if (calibration.Measuring)
            ImGui.TextUnformatted($"Recording: {calibration.SecondsLeft(Now):0.0} s left");
        var suggestion = calibration.Suggestion;
        if (suggestion is not null)
        {
            ImGui.TextUnformatted($"Idle RMS p95: {suggestion.Noise95:0.00000}   Soft RMS p25: {suggestion.Soft25:0.00000}");
            ImGui.TextUnformatted($"Normal RMS median: {suggestion.NormalMedian:0.00000}");
            ImGui.TextUnformatted($"Suggested gate: {suggestion.Gate:0.00000}; stable frames: {suggestion.StableFrames}");
            ImGui.TextWrapped(suggestion.Warning);
            if (ImGui.Button("Apply suggested gate and stability"))
            {
                config.RmsGate = suggestion.Gate;
                config.StableFrames = suggestion.StableFrames;
                SettingsChanged();
            }
            ImGui.TextDisabled("YIN threshold, release time, and minimum interval are unchanged. Validate with a detection test.");
        }
        DrawLivePitch();
    }

    private void DrawDetectionTest()
    {
        ImGui.BeginDisabled(labelledTest.Running);
        if (ImGui.BeginCombo("Test target", testString < 0 ? "All six open strings" : OpenStrings.Names[testString]))
        {
            if (ImGui.Selectable("All six open strings"))
                testString = -1;
            for (var i = 0; i < 6; i++)
                if (ImGui.Selectable(OpenStrings.Names[i]))
                    testString = i;
            ImGui.EndCombo();
        }
        ImGui.SliderInt("Picks per string", ref testRepeats, 1, 10);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(detector.SampleRate <= 0);
        if (ImGui.Button("Start labelled test"))
        {
            EnterTool();
            testSaved = false;
            reportStatus = "";
            testMetadata = new { startedUtc = DateTime.UtcNow, device = detector.ActiveDevice,
                sampleRate = detector.SampleRate, channel = detector.ActiveChannel, settings = config.GetDetectionSettings(),
                initialDroppedAudioBlocks = detector.DroppedAudioBlocks, initialDroppedFrames = detector.DroppedDetectionFrames };
            labelledTest.Start(testString, testRepeats, Now);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!labelledTest.Running);
        if (ImGui.Button("Cancel test"))
        {
            labelledTest.Cancel();
            reportStatus = "Test cancelled; partial results remain visible but are not saved.";
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Mute until the one-second countdown finishes. At PLAY, pick exactly once and let it ring. " +
            "Each listening window lasts two seconds; mute again for the next trial.");
        ImGui.TextUnformatted($"Trial {labelledTest.TrialNumber}/{labelledTest.TotalTrials}: {labelledTest.Expected}");
        ImGui.TextUnformatted(labelledTest.Phase switch
        {
            "mute" => "MUTE all strings to arm the next cue.",
            "count-in" => $"Ready... {labelledTest.SecondsToCue(Now):0.0} s",
            "listen" => $"PLAY {labelledTest.Expected} ONCE now",
            "complete" => "Test complete.",
            "cancelled" => "Cancelled.",
            "failed" => "No quiet input detected for 15 seconds. Check capture, mute the strings, or adjust the noise gate and restart.",
            _ => "Ready to start.",
        });
        var trials = labelledTest.Trials;
        ImGui.TextUnformatted($"Trials finished: {trials.Count}   No accepted detection: {trials.Count(t => t.Accepted.Length == 0)}");
        ImGui.TextUnformatted($"Windows with no audio frames: {trials.Count(t => t.Frames == 0)} (check capture; these are not reliable pick tests)");
        ImGui.TextUnformatted($"No correct detection: {trials.Count(t => t.Correct == 0)}   Octave errors: {trials.Sum(t => t.OctaveErrors)}   " +
            $"Other wrong notes: {trials.Sum(t => t.WrongNotes)}   Extra detections: {trials.Sum(t => t.ExtraDetections)}");
        ImGui.TextWrapped("No detection is not proof you missed a pick. Extra detections are duplicate candidates only " +
            "if you followed the one-pick instruction. Wrong octave means the right pitch class in another octave.");
        ImGui.TextWrapped(reportStatus);
        ImGui.TextWrapped("Completed tests automatically save a local JSON report in training-reports next to your diagnostics folder. " +
            "Reports include expected/received notes, Hz, cents, trial windows, settings, and dropped-data counters; no raw audio.");
        if (ImGui.Button("Copy test result summary"))
            ImGui.SetClipboardText($"Trials={trials.Count}; no detection={trials.Count(t => t.Accepted.Length == 0)}; " +
                $"octave errors={trials.Sum(t => t.OctaveErrors)}; other wrong={trials.Sum(t => t.WrongNotes)}; extras={trials.Sum(t => t.ExtraDetections)}\n{reportStatus}");
        DrawLivePitch();
    }

    private void StartMetronome()
    {
        StopTools();
        config.PracticeMode = true;
        ResetPractice();
        beats.Start(Now, config.MetronomeBpm);
        if (config.AudibleMetronome)
            clickPlayer.Start(config.MetronomeBpm);
        save();
    }

    private void DrawMetronome()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Optional rhythm drill: pick each prompted string on a beat, muting between beats.");
        var bpm = config.MetronomeBpm;
        var audible = config.AudibleMetronome;
        var offset = config.TimingOffsetMs;
        var changed = ImGui.SliderInt("Tempo (BPM)", ref bpm, 40, 160);
        changed |= ImGui.Checkbox("Audible click (Windows default output)", ref audible);
        if (changed)
        {
            config.MetronomeBpm = bpm;
            config.AudibleMetronome = audible;
            if (beats.Running)
                StartMetronome();
            save();
        }
        if (ImGui.SliderInt("Timing offset compensation (ms)", ref offset, -250, 250))
        {
            config.TimingOffsetMs = offset;
            if (beats.Running)
                StartMetronome();
            save();
        }
        if (ImGui.Button("Start rhythm / 4-beat count-in"))
            StartMetronome();
        ImGui.SameLine();
        if (ImGui.Button("Stop rhythm"))
            StopMetronome();
        if (beats.Running)
        {
            var beat = beats.BeatAt(Now);
            ImGui.TextUnformatted(beat < 4 ? $"COUNT IN: {Math.Max(1, beat + 1)}/4" : $"BEAT {beat % 4 + 1}   Next shape: {practice.Chord.Name}");
            ImGui.ProgressBar((float)(1 - beats.PhaseAt(Now)), new Vector2(-1, 16));
        }
        ImGui.TextUnformatted($"Timing only: {beats.OnTimeNotes}/{beats.ScoredNotes} within +/-80 ms; " +
            $"mean absolute offset {beats.MeanAbsoluteOffsetMs:0} ms; last {beats.LastOffsetMs:+0;-0;0} ms");
        ImGui.TextWrapped("Enable alternating chords above for change drills. Pitch accuracy stays separate. " +
            "Offsets use the first agreeing detector frame versus a software beat grid and include capture/playback latency. " +
            "Only detected checked notes are scored; unplayed beats are not counted as misses. " +
            "Positive compensation subtracts lateness; this is not lab-grade attack timing. Use headphones to avoid click bleed.");
        if (clickPlayer.Error.Length > 0)
            ImGui.TextWrapped(clickPlayer.Error);
    }

    private void DrawHistory()
    {
        ImGui.TextWrapped(history.Status);
        var suggestion = history.Recommend();
        ImGui.TextUnformatted($"Suggested next exercise: {suggestion.First}{(suggestion.Second.Length > 0 ? " / " + suggestion.Second : "")}");
        ImGui.TextWrapped(suggestion.Reason);
        if (ImGui.Button("Practise suggested exercise"))
        {
            StopTools();
            var first = Array.FindIndex(Chords.All, c => c.Name == suggestion.First);
            var second = Array.FindIndex(Chords.All, c => c.Name == suggestion.Second);
            if (first >= 0)
            {
                config.PracticeMode = true;
                config.PracticeChord = first;
                config.TransitionChord = Math.Max(0, second);
                config.AlternateChords = second >= 0 && first != second;
                ResetPractice();
                save();
            }
        }
        ImGui.TextWrapped("History measures checked pitches, not physical fingering or musical fluency. " +
            "Suggestions prioritise observed miss rates after at least five checked notes, then less-practised pairs.");
        ImGui.BeginChild("practice_history", new Vector2(0, 320));
        foreach (var session in history.Sessions.AsEnumerable().Reverse())
        {
            ImGui.TextUnformatted($"{session.StartedUtc:yyyy-MM-dd HH:mm} UTC | {session.Pair} | checks {session.CompletedChecks} | " +
                $"notes {session.Correct}/{session.Attempts}");
            foreach (var miss in session.StringMisses.OrderByDescending(p => p.Value).Take(3))
                ImGui.TextUnformatted($"  {miss.Key}: {miss.Value} misses");
            ImGui.Separator();
        }
        ImGui.EndChild();
        ImGui.TextWrapped("Stored locally in practice-history.json next to diagnostics. The most recent 200 sessions are retained; nothing is uploaded.");
    }
}
