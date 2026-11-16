# FFTrueBard

Practise open guitar chords and inspect your guitar's pitch inside Final Fantasy
XIV. WASAPI audio capture and YIN pitch detection are built into the Dalamud
plugin. No Python or separate detector app is needed.

> **Note:** This is a preview feature currently under active development.

**Game keyboard/mouse injection is not implemented.** Controller mappings only
produce diagnostic messages. Practice mode blocks that mapping path entirely.

## Install and update

Use XIVLauncher with Dalamud, then install from this custom repository:

1. Open `/xlsettings` and select **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:
   ```text
   https://github.com/claderino/FFTrueBard/releases/latest/download/pluginmaster.json
   ```
3. Save, open `/xlplugins`, and install **FFTrueBard**.
4. Open `/ftruebard` to select your guitar input and practise.

Successful pushes to `main` publish a versioned build at the same URL. On each
PC, refresh the plugin installer and use its update controls when an update is
available. This is a pull-based update feed, not an immediate remote push.

## Select your audio input

Use a clean guitar signal for the most reliable measurements:

1. Connect the NUX Mighty Space over USB and select **Dry Out** routing.
2. In `/ftruebard`, open **Audio & tuning** and select your input device.
3. Select the interface channel carrying the dry guitar. The status line shows
   the actual channel and native sample rate in use.
4. Play one note and inspect the live frequency, note, and signal level.

The initial device search matches `NUX`. A missing device displays a message
instead of silently switching to your microphone. You can explicitly select
**Windows default** if needed. Switching devices/channels stops an active debug
recording; start another recording after the switch.

## Edit note mappings

The **Mappings** tab edits saved note-to-key/mouse assignments without rebuilding
or editing configuration files. **Bindings are log-only:** this build does not
send keyboard events or clicks, and it has no tap/hold behaviour yet.

1. Select **Edit** beside a saved row, or enter a note and octave for a new row.
   You can also choose a note from the dropdown.
2. Optionally select **Pick a note to select it**, mute all strings, then pick
   one isolated note. This fills the draft note after a confirmed detection;
   it never saves a binding automatically. Selection times out after 15 seconds.
3. Choose **Ignore**, **Keyboard**, or **Mouse**. For keyboard bindings, choose
   a key and optionally add Ctrl, Shift, and Alt. Mouse buttons 1 through 5 are
   supported; mouse movement and mouse modifier combinations are not.
4. Review the draft and select **Save binding**. Updating a note replaces only
   that note's assignment. Changing the note adds/updates the new note rather
   than renaming or deleting the original row.
5. To delete a row, select **Remove**, then **Confirm removal**. Other rows remain
   unchanged. Use the filter to find a saved note or action.

Note names are canonicalised: `Bb3` saves as `A#3`, and `e2` saves as `E2`.
Supported rounded detector labels are C#2 through E6; the actual frequency range
is 70 through 1300 Hz. Audio identifies pitch, not physical string/fret position:
the same note on different strings shares a mapping, but different octaves do
not. Several notes may intentionally share one action.

Bindings retain the existing dictionary format, for example:

```json
{"E2":"Mouse1","F2":"Key:W","G3":"Key:Ctrl+Shift+1","E4":""}
```

Empty actions mean **Ignore**. Existing bindings are preserved. Unsupported
stored actions and noncanonical note labels remain visible for manual repair;
unsupported actions are not dispatched even in log-only mode. The editor
supports individual keys/combinations, not action sequences or scripts.

The bottom of the tab previews the current saved mapping for the last accepted
note, including its frequency and timestamp. Practice and training tools block
mapping previews by design. To inspect a `log-only` result, stop active tools,
turn off **Practice mode**, and enable **Enable mapping preview**. Play a note;
no physical input is sent. Previewing an old note never re-sends it. The debug
JSONL log records the actual resolution when each new accepted note arrives.

Mappings save in your local Dalamud configuration and survive plugin updates.
They do not automatically sync between PCs. Note selection pauses chord checks;
use **Stop tools and resume chord checks** when finished.

## Practise chords

The **Chord practice** tab provides Em, Am, C, G, D, E, A, and Dm shapes for
standard E A D G B e tuning without a capo.

1. Keep **Practice mode** enabled and choose a chord.
2. Follow the fret/finger table. `0` means open; `X` means do not play.
3. Hold the chord shape, mute all strings, and pick the prompted string.
4. Mute all strings between picks. A correct note within the cents tolerance
   advances to the next string; a wrong note stays on the same string.
5. Select **Retry current shape** to repeat the exercise.
6. Enable **Alternate two chords** and select two different shapes to practise
   changing between them. Completed checks and misses accumulate for the session.

The duration shown is the whole guided check, including picking and muting, not
just the time needed to change fingering. **Reset session** clears these stats.

This is single-note verification, not polyphonic chord recognition. Strumming
multiple strings can confuse it. The plugin checks expected pitch and octave,
not physical string identity, posture, clean fretting, or whether an `X` string
is correctly muted. Use your ears when strumming after a check.

## Tune your guitar

The **Tuner** tab provides an open-string tuner with a cents needle. Turn on
**Pause chord checks while tuning** to avoid grading tuning notes as practice.

1. Choose **Auto: nearest open string**, or select a string explicitly.
2. Pick that open string by itself and let the attack settle.
3. Adjust gently toward the centre. Within five cents, after at least three
   valid frames, the display reports **In tune**.

The target is standard tuning with A4 = 440 Hz. Auto mode only matches notes
within 150 cents of an open string; select a string manually if tuning is
further off. The needle uses a five-frame median to reduce flicker. Manual
selection reports octave mistakes rather than treating them as in tune.

## Calibrate detection

The **Calibration** tab measures your current input without changing settings
automatically. A working capture device is required to start.

1. Select **Start / restart calibration** and mute every string for five seconds.
2. Select **Record soft E2 picks**. Pick low E several times softly, muting
   between picks, during the five-second recording.
3. Select **Record normal E2 picks** and repeat at normal strength.
4. Review the noise floor, soft-note level, suggested gate/stability, and any
   warnings. Select **Apply suggested gate and stability** only if appropriate.
5. Validate the result with a labelled detection test.

Keep interface gain and guitar volume unchanged throughout. The suggested gate
lies between the measured idle noise and soft notes. Overlapping signal/noise
or insufficient audio produces no suggestion. Clipping produces a warning;
lower gain and repeat. Stability suggestions use pitch consistency; too few
pitched frames fall back to two frames. Other detection settings are unchanged.
Changing device/channel invalidates an existing calibration suggestion.

## Run a labelled detection test

The **Detection test** tab compares prompted notes with what the detector
accepts. Select one open string or all six, with one to ten trials per string.

1. Select **Start labelled test**.
2. Mute all strings until the one-second countdown completes.
3. At **PLAY**, pick the prompted note exactly once and let it ring during the
   two-second listening window.
4. Mute again to arm the next trial. Early playing resets the countdown.
5. Read the summary and use **Copy test result summary** to share it.

The summary separates no accepted detection, no correct detection, wrong
octaves, other wrong notes, and extra detections. A missing detection does not
prove you missed a physical pick. Extra events are duplicate candidates only
when you follow the one-pick instruction. Trials with no audio frames are
distinguishable from trials whose audio stayed below the gate.

Completed tests automatically save JSON reports under `training-reports` next
to `diagnostics`, including device/settings, expected notes, trial windows,
accepted frequency/cents measurements, and dropped-data counts. No raw audio
is saved or uploaded. Cancelled tests retain partial results on screen but do
not save a completed report. A 15-second wait without quiet input stops a test
so you can check capture and gate settings. Device or detection-setting changes
cancel active measurements to avoid mixing different setups in one test.

## Add rhythm to practice

The bottom of **Chord practice** contains an optional visual metronome and
rhythm scoring. Select two alternating chords above for a guided change drill.

1. Set a slow tempo, starting around 40 to 60 BPM.
2. Optionally enable **Audible click** through Windows' default playback device.
3. Select **Start rhythm / 4-beat count-in**.
4. After the count-in, pick each prompted string on a beat and mute between
   beats. Change shapes when the guided check advances.
5. Select **Stop rhythm** to return to untimed practice.

Timing is separate from pitch accuracy: it reports offsets from the software
beat grid and the number within 80 ms. Only detected checked notes are scored,
once per beat; unplayed beats are not counted as misses. This is guided
single-string rhythm practice, not strummed-chord recognition.

Audio clicks use a sample clock rather than render-frame timing. Scores use the
first agreeing pitch frame, so capture/playback latency and frame granularity
still affect them. **Timing offset compensation** subtracts consistent lateness;
it is a manual adjustment, not automatic latency measurement. Headphones help
prevent clicks from leaking into capture. Tempo/audio/offset changes restart
the count-in and scores. Closing the window or starting another training tool
stops the metronome. Without playback hardware, visual practice still works.

## Review practice history

The **History** tab stores checked-note accuracy, completed checks, chord pairs,
and the strings on which mistakes occurred. It saves locally after each checked
note to `practice-history.json` next to `diagnostics`, retaining the latest 200
sessions across plugin reloads. Restarting a practice session or changing
exercises begins a new history entry on the next checked note.

Suggestions prioritise the highest observed miss rate among exercises with at
least five checked notes. Otherwise, they choose a less-practised beginner pair.
Select **Practise suggested exercise** to load it. These are simple practice
suggestions, not assessments of physical fingering or musical fluency. Existing
unreadable history is not overwritten; the UI reports the load/save problem.

Tuning, calibration, and labelled tests pause chord grading and block mapped
inputs. After finishing a tool, use **Stop tools and resume chord checks** to
resume. No training reports or history are uploaded.

## Tune detection live

The **Audio & tuning** sliders apply without rebuilding and save to your local
Dalamud configuration:

| Control | Meaning |
| --- | --- |
| RMS noise gate | Set above idle noise but below a soft picked note. |
| YIN threshold | Lower is stricter about periodicity. |
| Stable frames | Consecutive matching notes before acceptance; more adds delay. |
| Minimum interval | Minimum time between accepted notes. |
| Quiet time to rearm | Silence needed before the same note can trigger again. |

A sustained note doesn't auto-repeat. The live display includes Hz, cents from
the nearest equal-tempered note (A4 = 440 Hz), RMS, peak, periodicity confidence,
and the detector's decision. Confidence is not a probability of correctness.
Lower input gain if the clipping warning appears.

## Record diagnostics

The **Debug log** tab records local JSON Lines files for tuning and reporting
problems. Logging starts off every time the plugin loads.

1. Enable **Record frequency diagnostics to JSONL**.
2. Record a short idle period, isolated notes, then a guided chord check.
3. Disable recording to flush the file.
4. Select **Copy last log file path** to locate and share the file if desired.

The folder is `diagnostics` inside Dalamud's FFTrueBard configuration directory;
the UI displays its exact path. Logs include device/channel/rate, UTC Unix-ms
timestamps, frequency, note/octave, cents, RMS/peak, YIN confidence, settings,
practice target/tolerance/result, mapping status, and dropped-data counters.
Rejected and sustained frames are included, not just accepted notes.

Detector decisions are `gated`, `unvoiced`, `stabilizing`, `cooldown`, `sustained`,
and `accepted`. A typical frame row contains:

```json
{"type":"frame","frame":{"frequency":110.0,"note":"A2","cents":0.0,"decision":"accepted"}}
```

Each file starts with a session header and ends with a dropped-log-row count.
Audio and event queues are bounded; nonzero dropped counters indicate missing
data. Each recording stops automatically at approximately 20 MiB. Files are not
automatically deleted or uploaded. No raw audio, chat, or game state is recorded;
device names can still identify your hardware, so inspect logs before sharing.

## Development and tests

The plugin targets .NET 10 and Dalamud API 15. With the matching .NET SDK and
Dalamud development distribution available, build using:

```powershell
dotnet build FFTrueBard/FFTrueBard.csproj -c Release
```

The dependency-free regression suite does not need FFXIV or an audio device:

```powershell
dotnet run --project tests/FFTrueBard.Tests.csproj -c Release
```

Tests cover pitch accuracy across sample rates and harmonics, noise rejection,
stability/gating/rearming, chord progression, JSONL recording, tuner readings,
calibration, labelled trials, rhythm scores/click synthesis, and history
persistence/recommendations, mapping validation, binding persistence, preview
routing, and note selection. CI runs these
checks before building and publishing the plugin with its NAudio dependencies.
Real guitar hardware and the in-game UI still require manual testing.

## Limitations

Dalamud and other third-party tools are against FFXIV's terms of service. This
project does not provide protection from account enforcement.

Future work includes real keyboard/mouse input and polyphonic chord assessment.
These features are not part of this build.
