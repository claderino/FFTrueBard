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
stability/gating/rearming, chord progression, and JSONL recording. CI runs these
checks before building and publishing the plugin with its NAudio dependencies.
Real guitar hardware and the in-game UI still require manual testing.

## Limitations

Dalamud and other third-party tools are against FFXIV's terms of service. This
project does not provide protection from account enforcement.

Future work includes real keyboard/mouse input, an in-game mapping editor, and
polyphonic chord assessment. These features are not part of this build.
