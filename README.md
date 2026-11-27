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
2. In `/ftruebard`, open **Input → Setup** and select your input device.
3. Select the interface channel carrying the dry guitar. The status line shows
   the actual channel and native sample rate in use.
4. Open **Input → Live**, play one note, and inspect its pitch and signal level.

The initial device search matches `NUX`. A missing device displays a message
instead of silently switching to your microphone. You can explicitly select
**Windows default** if needed. Switching devices/channels stops an active debug
recording; start another recording after the switch.

## Use your local lesson library

The **Lessons** tab plays your own MP3 practice tracks and opens your lesson PDF
in your Windows PDF viewer. No book, notation, or audio is included in the plugin
or downloaded from GitHub. You must provide your own local copies on each PC.

To set up the library:

1. Open `/ftruebard` and select **Settings → Lesson files**.
2. Use **Browse** next to **Lesson root folder**, or paste an absolute path.
   For Guitar Aerobics, choose the folder containing the book PDF and the
   `_Exercises` and `_Rhythm` folders.
3. If the files are stored separately, expand **Individual locations /
   overrides** and select the **Lesson PDF file**, **Exercise MP3 folder**, and
   **Rhythm MP3 folder** individually. Leave an override blank to use discovery
   under the root. Clear old overrides when moving to another root.
4. Select **Save locations and scan**. Review the resolved locations and any
   missing-file messages. Multiple PDFs require an explicit file choice.
5. Select **Open local lesson PDF** to read the book in your external viewer.

Each PC saves its own locations. Files stay in place; the plugin does not copy,
move, modify, or upload them. Scanning runs outside the game thread and lists
up to 1,000 MP3 files directly in each audio folder, not nested subfolders.
Web URLs and network shares are not supported. If you move the files, update
the paths and scan again. Lesson paths are not included in diagnostic snapshots,
and neither lesson media nor progress is automatically attached to exports.

### Play and loop a track

The player uses the Windows default playback device. Use headphones and the
clean guitar capture channel so the detector does not hear the backing track.

1. Choose an exercise and open **Lessons → Audio**. Expand **Browse exercise
   and backing tracks**, then select **Load selected week's exercise track**.
   Recognized Guitar Aerobics filenames connect exercises
   1–365 to the weekly MP3s. Unknown filenames and the tuning track remain
   available under **All exercise / tuning tracks**.
2. Alternatively, expand **Rhythm tracks by style / BPM** and select a track.
   Style and BPM come from filenames; BPM filter `0` lists all tempos.
3. Select **Play lesson audio**. Use **Pause audio**, **Stop audio**, the seek
   slider, and **Lesson playback volume** as needed.
4. Expand **A-B loop**. Set **Loop A** and **Loop B** in seconds, or use **A = current** and
   **B = current**. Check **Enable A-B loop**, then select **Apply loop
   boundaries**. B must be at least 0.1 seconds after A and inside the track.
   To disable a loop, clear the checkbox and apply again.

Loading does not start playback automatically. Weekly tracks contain several
exercises: use an authored local lesson card's cue or set the boundaries
yourself. No automatic timestamp extraction, PDF page jumps, notation
extraction, speed changes, or playback-synchronized grading are provided.
Loop boundaries operate on decoded audio frames; seeking
and boundary changes can take about one output buffer to become audible.

Lesson playback pauses chord grading and blocks mapped inputs. Closing the
plugin window, restarting capture, or starting another training tool stops
playback. After playback, use **Resume chord checks** to resume
chord practice. If you change the Windows output device, reload the track.

### Save lesson progress

Progress is your own assessment, separate from detected-note accuracy.

1. Choose an exercise and open **Lessons → Notes**. Select **Mark exercise
   complete** if appropriate, and enter **Personal notes**.
2. Select **Save progress / notes**, or **Discard unsaved lesson edits**.
   Save or discard before changing exercises. Unsaved edits are not saved on
   plugin reload.

Progress and notes save in `lesson-progress.json` next to `diagnostics`. The
previous saved version is retained as `lesson-progress.json.bak`. They do not
automatically sync between PCs. Each exercise supports up to 2,000 characters
of notes. A marked completion does not mean the detector verified the exercise.

### Follow guided exercise cards

The exercise framework reads a local catalog containing lesson steps,
self-checks, PDF references, optional audio cues, and optional pitch-data
references. Adding more cards does not require a plugin update. The plugin
does not automatically turn the PDF into tutorials or claim that every
exercise has been implemented.

To use a catalog:

1. Save your authored `exercises.course.json` in `fftruebard-lessons` under
   the saved lesson root, or beside the PDF when no root is set.
2. Open **Settings → Lesson files** and select **Save locations and scan**.
   Alternatively, expand **Exercise catalog**, browse to another local `.course.json`
   file, and select **Save catalog location and reload**. Reload after
   editing the file.
3. In **Lessons**, use the exercise dropdown or the arrow buttons.
   **Filter local cards** narrows the list by week or technique.
   Enter a number and select **Go to exercise** to jump to any of the 365
   exercise numbers.
4. Open **Guide** and select **View tablature** to see the exercise's notation
   from your local PDF.
5. Select **Prepare guided practice** to pause other training tools and chord
   scoring. Follow the steps and open **Self-checks** for your own observations.
   Capture does not need to be running for manual practice.
6. Save useful observations in your personal notes and choose whether to mark
   the exercise complete. Step position and self-check ticks are not saved;
   they reset on exercise selection, note discard, or catalog reload.

Exercises without a guided card can still show an indexed book excerpt and
play weekly audio; they don't have guided steps or automatic grading. A
`guided` card disables pitch grading, even if a standalone note-file override
is set. This supports bending, legato, ringing arpeggios, strummed chords,
and other exercises without pretending to assess their technique. A
`pitch-order` card can offer the existing slow, mute-separated pitch pilot
after you review its note data. This label does not certify technique
detection or enable rhythm grading.

For cards with audio cues, select **Load exercise cue** in **Lessons → Audio**.
After decoding, the player seeks to the cue start and fills the A/B controls
without enabling a loop. Press **Play lesson audio** to listen, and explicitly
enable/apply the loop if wanted. Playback continues beyond the suggested B
point unless looping is enabled or you stop it. **Stop audio** returns to
the track start; reload the cue to jump back to the exercise.

Cue ranges are checked against the decoded track duration. Out-of-range
values are rejected, not silently clamped. Book timestamps can be approximate;
listen and adjust them locally. Loading a cue never starts playback or
changes detector settings.

### Read tablature in the plugin

Open **Lessons → Tablature** to display notation from your selected local PDF.
The reader starts at **Fit**. Use **+** or **−** to change the zoom, and
**Full page** or **Excerpt** to switch views when a crop is available. For an
unlinked exercise, enter the PDF page number yourself. **Open book externally**
opens your usual PDF viewer.

For the supported Guitar Aerobics PDF, click the printed speaker or timestamp
to play only that exercise. The reader uses the next printed timestamp (or
the end of the weekly MP3) by default. When a matching reviewed timing map
shows that the printed cutoff would truncate the phrase, playback uses the
measured end instead. Click the speaker again to pause or resume. Playback
includes the recorded announcement and count-in without splitting the file.

Use **Playback speed** above the TAB or in **Lessons → Audio** to choose
0.50× to 1.50× without changing pitch. The setting is saved and works with
seeking and A–B loops. Choose **1.00× (normal)** to restore the original speed.

The plugin includes reviewed playhead timings for all 365 exercises. They work
with the supported Guitar Aerobics PDF and matching weekly MP3s; no separate
JSON file is needed. A matching local `exercise-NNN.playhead.json` in
`fftruebard-lessons` takes priority.

With a matching note map, the vertical TAB playhead appears at the first
note, not during the count-in. Click a fret to seek to its onset, or drag
across the strings to move within the phrase. If a fret is played more than
once, seeking uses the current pass or the nearest occurrence. When no map
matches, use the audio-only scrub strip below the strings.

To loop part of a note-synced exercise, start playback from the printed speaker.
Right-click the first fret and select **Set A at note start**. Right-click the
last fret and select **Set B at note end**, then reopen the menu and select
**Play A–B loop**. Use **Turn off TAB loop** to continue within the exercise.
For repeated notes, set the points while listening to the pass you want to loop.

The plugin renders one view at a time in memory. It doesn't copy or upload
pages, infer note timing from the notation, or grade what you play from it.

For the supported Guitar Aerobics PDF, the plugin includes a coordinate-only
index for all 365 exercises. Select your book in **Settings → Lesson files**,
then select **Save locations and scan**. No JSON file is needed. The index
contains no book pages, text, notation, audio, or guided lesson cards.

The built-in index matches the exact PDF by SHA-256, not by filename. For a
different PDF, use **View page** to select a page manually or put your own
`<book-name>.tablature.json` beside `<book-name>.pdf` and rescan. A valid local
index takes precedence and also must match the PDF.

An index uses `schemaVersion: 1`, a `pdfSha256` hex string, and an `exercises`
array. Each entry has `exercise`, `pdfPage`, and `crop` (`x`, `y`, `width`,
and `height` in page fractions, measured from the top-left). A guided card's
explicit page and crop take precedence.

### Practise your own tabs

Open **My Tabs** in `/ftruebard` to read your own TAB PDFs. The reader first
looks in `Downloads/tabs`. To use a different folder, choose it with **Browse**
or enter an absolute path, then select **Save folder & scan**. Select a song,
then use **Previous**, **Next**, and the zoom controls to read it.

When the PDF has a matching reviewed `.tab-guide.json` transcription,
**Play TAB audio** generates a guitar guide from its frets, tuning, capo, and
timing. Click or drag across a transcribed TAB staff to seek. Right-click the
first and last frets to set A and B, then choose **Play A–B loop**. Use
**Playback speed** to slow the guide down without changing pitch. The reader
shows which passage is covered; other parts of the PDF remain viewable.

Use **Metronome** when a score has no transcription. You can also put a
Standard MIDI file beside its matching PDF, such as `Evening Study.mid` beside
`Evening Study.pdf`, and rescan. An **Unsynced MIDI guide** plays without
following the PDF. Neither mode grades your playing. The reader does not
send your PDFs, transcriptions, or MIDI files to a service.

In **Build practice guide**, choose **Scan whole PDF** to check what can be
read automatically. When a score qualifies, **Preview detected score
(unreviewed)** plays the result with the same seeking, speed, and loop
controls. Previewing never saves or replaces a guide.

To review a passage, select **Detect notation on this page** and choose a
staff. Check the detected frets, note lengths, rests, tempo, capo, and tuning.
Click a printed column to edit it. You can also insert a missed column or
delete a false detection. Lengths and rests use quarter-note beats (`0.5`
for an eighth note). Audition a chord or preview the passage, then confirm
your review and select **Save reviewed guide**.

The TAB scanner handles some digital exports, including some outlined
notation. It doesn't read arbitrary scans or reliably interpret ties,
repeats, and multiple voices. It leaves unread lengths unset and can
withhold a whole-score preview.

For a supported engraved piano PDF, open **Piano melody to guitar TAB** and
select **Read piano opening**. The preview takes the highest note at each
attack on the treble staff and omits the bass and inner chord notes. Select
**Copy melody TAB** for the text arrangement, or **Play melody preview** to
follow the original score, change speed, and loop a passage. The arrangement
uses standard tuning without a capo. This reader is limited to the opening
system of some one-sharp, 4/4 scores; it stops at the first measure it can't
read. A preview doesn't modify the PDF or save a reviewed guide.

Guides are saved beside the PDF. To extend a guide, append another reviewed
staff in playback order using the same tuning and capo. Appending or
replacing keeps a backup of the old guide.

For a local transcription, use `<pdf-name>.tab-guide.json` beside the PDF.
Version 1 stores `pdfSha256`, `reviewed`, a short `coverage` label, six MIDI
`tuning` pitches from low to high, `capo`, and `rows`. Each row stores the
1-based `page`, normalized `left`, `top`, `right`, and `bottom`, and its
`attacks`. An attack has `start` and `end` in seconds, normalized `x` and
`width`, and a zero-based `pass`. Its `frets` use `string` (1 is the highest
string), `number` (fret relative to the capo), `hold` in seconds, and optional
`delay` in seconds for a rolled chord. Set `schemaVersion` to `1` and mark the
transcription `reviewed` only after checking the notes against the PDF.

### Local catalog format

Catalogs are declarative data, not scripts. The following independent card
illustrates the format without including any Guitar Aerobics lesson content:

```json
{
  "schemaVersion": 1,
  "title": "Personal guitar course",
  "source": "My practice plan",
  "exercises": [
    {
      "exercise": 1,
      "title": "Controlled picking",
      "technique": "Picking",
      "steps": [
        "Read the chosen phrase in my notation.",
        "Listen, then practise slowly."
      ],
      "selfChecks": ["The intended strings sound cleanly."],
      "pdfPage": 5,
      "tablatureCrop": { "x": 0.05, "y": 0.5, "width": 0.9, "height": 0.25 },
      "printedPage": "4",
      "minBpm": 40,
      "maxBpm": 80,
      "mode": "guided",
      "audio": { "track": "my-weekly-audio.mp3", "startSeconds": 2, "endSeconds": 8 }
    }
  ]
}
```

The schema accepts 1–365 cards with unique exercise numbers in the range
1–365. The catalog is limited to 2 MiB, its title to 200 characters, and its
source description to 500. Each card requires a title (up to 200 characters),
a technique label (100), and 1–20 nonempty steps (1,000 characters each).
Self-checks are optional, with up to 20 entries of 500 characters each.

`pdfPage` uses the PDF viewer's one-based page number. The preview checks it
against the configured book when it opens. `tablatureCrop` is optional and
requires `pdfPage`. Its four values are fractions of the page, with `x` and
`y` measured from the top-left. If omitted, the preview shows the full page.
`printedPage` can contain a label such as a Roman numeral. Tempo references
must supply both `minBpm` and `maxBpm`, in ascending order from 20 to 400.
`rhythmGuide` is optional text of up to 300 characters. None of these fields
changes playback speed or scores musical timing.

`mode` defaults to `guided`. To offer pitch checking, use `pitch-order` and
optionally name a `pitchSequenceFile`, such as `personal-phrase.notes.json`.
Otherwise the card uses `exercise-NNN.notes.json`. Card note references resolve
beside the catalog; a saved absolute pitch-file override still takes precedence.
Note-file exercise IDs must match the selected card. Pitch files are reviewed
separately before a drill can start.

The optional `audio` object references a direct MP3 filename in the scanned
exercise-audio folder. An empty `track` selects the unique weekly track covering
the exercise number. `startSeconds` is required; `endSeconds` can be omitted
to use the actual track end. Missing or ambiguous tracks are reported. File
references cannot include directory traversal, absolute paths, URLs, or
alternate data streams.

Catalogs, course-derived instructions, note transcriptions, and media stay
outside the public repository and release assets. Catalog locations and
contents are excluded from diagnostic snapshots and automatic attachments.
The plugin reads but never edits the catalog. Personal progress remains
keyed by exercise number, not by catalog: switching catalogs does not create
a separate progress history. This release does not change note detection.

### Check pitch order visually

**Lessons → Pitch drill** checks one note at a time against a local,
reviewed sequence. This pilot is deliberately slow: pick, mute all strings,
wait for **Ready**, then pick again. It does not evaluate rhythm, picking
direction, fingering, or performance along with the MP3.

To start a drill:

1. Connect your dry guitar and check the signal in **Input → Live**.
2. In **Lessons**, choose an exercise and open **Pitch drill**.
3. Expand **Pitch targets and setup**. Leave **Pitch-sequence file override**
   blank to use a pitch-enabled catalog card's note file. Without a card,
   discovery uses `fftruebard-lessons/exercise-NNN.notes.json` under the saved
   lesson root (or beside the PDF when only a PDF location is set).
   Alternatively, browse to a local `.notes.json` file and select
   **Save sequence location and load**. Guided-only cards do not offer this
   pitch drill.
4. Use the **Note map** or **Review expected sequence** to compare targets with your
   lesson. Check **I reviewed these targets against my lesson**. An exercise
   number mismatch prevents loading; arbitrary local files are not certified
   by the plugin.
5. Select **Start drill**. This stops lesson playback
   and other training tools, pauses chord checks, and blocks mapped inputs.
6. Mute until **Ready**, then pick the prompted note. Mute again after every
   scored attempt, even when the next note has a different pitch.

Feedback includes labels as well as colors:

- **Green / correct:** the expected pitch and octave matched within your
  configured cents tolerance. The drill advances one target.
- **Red / wrong:** a clear event had the wrong pitch, octave, or tuning offset.
  The drill stays on that target for a retry.
- **Amber / uncertain:** input was unclear, clipped, stale, or interrupted.
  Uncertain events do not count as mistakes.

**Skip target** explicitly marks a target as unplayed. Silence is not
automatically treated as a missed note. Capture stops, dropped data, or a
1.5-second processing stall stop the drill without adding mistakes. Repeated
pitches cannot be distinguished from one ringing note without muting.
String/fret annotations are guides, not proof of the physical string played.

The display shows resolved targets, wrong-pitch retries, explicit skips, and
unresolved targets. Pitch-attempt accuracy is matched events divided by
matched plus wrong events; it excludes skips and includes retries. Uncertain
counts are processed frames, not missed picks. Run results remain on screen
until restart and are not saved as practice history or marked as lesson
completion. Closing the window, playing lesson audio, or starting another tool
stops the drill.

### Visualize the exercise and live input

In **Lessons → Pitch drill**, use the **Note map** to preview the order of
targets from the loaded local note file. Page through the tiles before
playing. During a pitch drill, the map follows the current target and labels
it **Mute** or **Ready**. It also shows matches, skips, and wrong attempts.
Optional `S` and `F` labels show the suggested string and fret, not what the
detector identified. Guided cards without pitch grading show the input graph
only. The map is not synchronized to lesson audio.

Expand **Live input graph** in **Pitch drill**, or open **Input → Live**,
to see the most recent 120 detector frames. The upper trace shows
RMS, the current noise gate, and clipping. The lower trace distinguishes pitch
estimates, accepted events, and accepted events with fresh-pick evidence.
Newest frames appear on the right; the graph is not an onset timing
measurement. It stores no raw audio or history on disk.

### Local pitch-data format

Pitch data is a small JSON file you provide on each PC. No course transcription
is bundled in GitHub releases. Use the following independent three-note example
as a format reference, not as Guitar Aerobics lesson content:

```json
{
  "schemaVersion": 1,
  "exercise": 1,
  "title": "Three-note pitch check",
  "source": "Personal practice targets",
  "notes": [
    { "midi": 69 },
    { "midi": 69 },
    { "midi": 71 }
  ]
}
```

Save it with a `.notes.json` extension. MIDI values use sounding pitches
(open high E is E4, MIDI 64), not octave-transposed guitar notation. Each file
supports 1–512 notes, MIDI values 40–87, and a maximum size of 256 KiB. The title
is limited to 200 characters and the source description to 500. Optional
`string` and `fret` values must appear together and agree with standard tuning:
string 1 is high E, string 6 is low E, and frets range from 0 to 24.

The plugin reads these files but never modifies them. Note files and their
locations are not included in diagnostic snapshots or automatic export
attachments. Opt-in detection logs can still contain detected pitches and
exercise/index feedback.

## Prepare for manual testing

**Settings → Testing & recovery** groups audio health, a guided checklist, settings
recovery, and diagnostic export. Test detection and mapping previews, not actual
FFXIV control: this build still does not send keyboard or mouse events.

### Check audio health

In **Input → Live**, expand **Capture health & drop counters** to see arriving
audio blocks, sample and processed-frame counts, accepted-note counts, last-frame
ages, RMS/dBFS, peak level, clipping, and dropped-data counters.

- **Silent input** means callbacks and processing frames are arriving, but the
  selected channel is nearly zero. Check routing if you are playing.
- **Below noise gate** means audio is arriving below the gate, as expected while
  muted. A soft note below the gate can need tuning.
- **No audio callbacks / capture stalled** means recent audio did not arrive.
  This does not establish that the guitar is silent.
- **Processing stalled** means callbacks arrived without recent pitch frames.
- **Stopped** means capture or processing is stopped/failed. Read the status
  message, then use **Restart capture** if appropriate.

Health uses a 1.5-second stale threshold and a two-second startup grace period.
Counts reset on capture restart except lifetime dropped counters. Signal-present
status is not a guarantee of correct pitch recognition.

### Follow the checklist

The checklist records your observations and per-step frame/note counts; it does
not decide whether your hardware passed automatically.

1. Select **Begin guided manual test**.
2. Select **Prepare step and open its tab**. Follow the instructions for silence,
   six open strings, five repeated low-E picks, one Em chord, then a mapping
   preview. The repeated-pick step prepares Detection test; select its Start
   button when ready.
3. Return to **Testing & recovery**, enter notes, and select **Pass**, **Issue**,
   or **Skipped** for the current step.
4. Select **Next step**, or **Finish checklist** on the last step.

Checkpoints save under `training-reports/manual-check-<id>.json` when you mark,
advance, cancel, or unload an active checklist. Notes are limited to 2,000
characters per step. A checklist continues collecting observations across tabs
until finished/cancelled; it is not resumed automatically after plugin reload.
Finish/cancel returns to Practice mode. Existing reports are not removed.

### Reset or undo settings

The **Reset and undo** section lists pre-edit recovery points. Mapping changes,
calibration **Apply**, capture-device/channel changes, detection slider gestures,
and resets save recovery points before changing settings. A slider gesture gets
one pre-edit point rather than one per rendered frame.

1. Select **Undo latest edit** or choose an older recovery point.
2. Review the confirmation text and select **Confirm settings change**.
3. Check the input status if a restore changes your device or channel.

Restores cover mappings, mapping-preview enablement, input device/channel, and
detection settings, not practice history or reports. They force Practice mode
on. Restoring also backs up the current state, so restoring the newest point
again can reverse the previous restore; select an older point for older edits.

**Reset detection defaults** under **Input → Setup → Detection tuning** and
**Reset mappings to original defaults** under **Settings → Mappings** both
require confirmation and a backup.
Detection reset preserves device/channel and bindings. Mapping reset replaces
all bindings with the original defaults. The latest 20 valid recovery files are
retained in `settings-backups`; unreadable files are skipped, not overwritten.
Edits are blocked if a recovery point cannot be written. Recovery files can
contain hardware names and are not automatically included in exports.

### Export diagnostics for review

The **Diagnostic export** section creates a local ZIP; nothing is uploaded.

1. Select **Refresh available logs and reports**, then explicitly select any
   diagnostic JSONL files or training/checklist JSON reports to attach. None are
   selected automatically; the picker lists the latest 100 eligible files.
2. Choose whether generated snapshots include device names (off by default).
   Existing selected files are **not redacted** and may contain device names,
   local paths, and your notes.
3. Select **Prepare contents preview**. This stops and flushes debug recording, then
   freezes selected file bytes and generated version/audio/settings snapshots.
4. Inspect the filenames, sizes, SHA-256 hashes, and file-content previews.
   Only the first 16 KiB of each file are displayed; inspect the original file
   before approving larger attachments.
5. Check **I reviewed these contents and want a local archive**, then select
   **Create local ZIP from this preview**.
6. Use **Copy ZIP path** to locate the file and decide whether to share it.

The ZIP contains `session-info.json`, `settings.json`, and exactly the selected
log/report bytes shown in the frozen preview, even if source files change later.
It excludes raw audio, credentials, and automatic history/backups attachments.
Exports have a 50 MiB uncompressed limit and are written under `exports` next to
`diagnostics`. Linked files/directories and paths outside the allowlisted
log/report folders are rejected. Export files are not automatically deleted.

## Edit note mappings

**Settings → Mappings** edits saved note-to-key/mouse assignments without rebuilding
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
use **Resume chord checks** when finished.

## Practise chords

**Practice → Chords** provides Em, Am, C, G, D, E, A, and Dm shapes for
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

**Input → Tuner** provides an open-string tuner with a cents needle. Turn on
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

**Input → Calibration** measures your current input without changing settings
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

**Input → Detection test** compares prompted notes with what the detector
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

Under **Practice → Chords**, expand **Rhythm & metronome** for visual timing
and rhythm scoring. Select two alternating chords above for a guided change drill.

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

**Practice → History** stores checked-note accuracy, completed checks, chord pairs,
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
inputs. After finishing a tool, use **Resume chord checks** to
resume. No training reports or history are uploaded.

## Tune detection live

The sliders under **Input → Setup → Detection tuning** apply without
rebuilding and save to your local Dalamud configuration:

| Control | Meaning |
| --- | --- |
| RMS noise gate | Set above idle noise but below a soft picked note. |
| YIN threshold | Lower is stricter about periodicity. |
| Stable frames | Consecutive matching notes before acceptance; more adds delay. |
| Minimum interval | Minimum time between accepted notes. |
| Quiet time to rearm | Silence needed to rearm when no fresh pick is recognized. |

A sustained note doesn't auto-repeat. The live display includes Hz, cents from
the nearest equal-tempered note (A4 = 440 Hz), RMS, peak, periodicity confidence,
and the detector's decision. Confidence is not a probability of correctness.
Lower input gain if the clipping warning appears.

Clear repeated picks can now retrigger the same pitch without full silence.
The detector looks for a level dip followed by a fresh attack, then still
requires stable pitch and the configured minimum interval. Attack evidence
expires after 250 ms and can justify only one acceptance. Soft repeats,
heavy compression, high stability settings, or fast playing can still be missed.
Deep tremolo or other sharp level changes can resemble picks; this is an
envelope heuristic, not a guaranteed physical-pick detector.

Pitch analysis applies a low-pass filter to an analysis copy to reduce
high-frequency pick noise. The raw samples, level meters, noise gate, and
saved tuning settings are unchanged. The lesson pitch-order pilot still
requires muting between attempts; these changes do not add rhythm grading.

## Record diagnostics

**Settings → Debug log** records local JSON Lines files for tuning and reporting
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
The `freshAttack` field identifies accepted events supported by the new
envelope detector. The existing `onsetTimestampMs` remains the first
agreeing pitch-frame timestamp, not a measured physical pick time.

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

The regression suite uses NAudio for in-memory playback tests but does not need
FFXIV or an audio device:

```powershell
dotnet run --project tests/FFTrueBard.Tests.csproj -c Release
```

Tests cover pitch accuracy across sample rates and harmonics, noise rejection,
stability/gating/rearming, chord progression, JSONL recording, tuner readings,
calibration, labelled trials, rhythm scores/click synthesis, and history
persistence/recommendations, mapping validation, binding persistence, preview
routing, note selection, audio-health states, manual checklists, settings
recovery, reviewed diagnostic archives, local lesson discovery/progress, and
sample-frame-aligned seeking and A-B looping, local pitch-sequence validation,
mute-separated pitch grading through the YIN detector, guided-catalog
validation/filtering, catalog cue/reference resolution, independent reference
scoring, native-channel replay of generated audio, ringing repeated picks,
and held-note, decay, vibrato, gain, and noise controls. CI runs these
checks before building and publishing the plugin with its NAudio dependencies.
Real guitar hardware and the in-game UI still require manual testing.

For an optional read-only decode check against your own lesson files, set
`FFTRUEBARD_LESSON_TEST_ROOT` to the local course root before running the tests.
This opens each discovered MP3 without playing it or copying its contents into
the repository. Normal CI does not require or include course materials.

### Measure the detector against reference recordings

The test runner can replay a local exercise recording through the actual
`PitchProcessor` and YIN estimator. This is a measurement mode, separate from
the regression suite and the in-game lesson drill. It does not change detector
settings in the plugin or declare the hardware ready.

For a meaningful reference case, independently review both the note sequence
and the recording's timing. A PDF cue can include a spoken introduction or
count-in. Notes printed on a page do not establish their exact onset times in
an MP3. Do not use the detector's own predictions to create its expected labels.
Avoid treating polyphonic rhythm backing tracks as single-note ground truth.

Save a local `.reference.json` file. The following shows its structure using
independent three-note practice targets; replace the paths and independently
check the timing intervals before setting `timingsReviewed`:

```json
{
  "schemaVersion": 1,
  "title": "Personal phrase reference",
  "audioFile": "C:\\Practice\\personal-recording.wav",
  "sequenceFile": "C:\\Practice\\personal.notes.json",
  "exercise": 1,
  "channels": [1],
  "analysisEndSeconds": 2.5,
  "timingsReviewed": true,
  "timingSource": "Notation and independent recording review",
  "timingUncertaintyMs": 20,
  "attackGuardMs": 40,
  "releaseGuardMs": 30,
  "pitchToleranceCents": 35,
  "timings": [
    { "startSeconds": 0.5, "endSeconds": 1.0 },
    { "startSeconds": 1.0, "endSeconds": 1.5 },
    { "startSeconds": 1.5, "endSeconds": 2.0 }
  ]
}
```

The existing `.notes.json` sequence supplies expected MIDI pitches in order.
There must be exactly one timing interval per note, including every repeated
note. Intervals are non-overlapping and measured in seconds from the start
of the source file. Gaps and unlabelled material are not note ground truth.
Guard margins must be at least as large as the declared timing uncertainty.
Labels can be approximate; the report does not claim exact pick-onset timing.

Run the measurement with absolute local paths and a new output directory:

```powershell
dotnet run --project tests/FFTrueBard.Tests.csproj -c Release -- `
  --reference-audio "C:\Practice\phrase.reference.json" `
  "C:\Practice\reference-results-001"
```

Input can be MP3 or WAV. Channels are one-based and processed separately at
the native rate, without stereo averaging, normalisation, or resampling.
The frame-size calculation matches the current capture worker. Audio before
the first label is processed as context, but is not counted as a guitar note.
Incomplete trailing frames are not padded. This bypasses WASAPI capture and
real-time scheduling, so it cannot verify USB delivery, device routing, or
in-game responsiveness.

The optional `settings` object captures `threshold`, `rmsGate`, `stableFrames`,
`retriggerMs`, and `releaseMs`. Omitted settings use the plugin's original
defaults: `0.15`, `0.004`, `2`, `150`, and `100`. The runner does not read the
user's current Dalamud configuration. Reports include those settings, source
file hashes, and the compiled runner hash for later comparisons.

The local output contains `report.json` and one pitch-frame CSV per selected
channel. No audio is copied or uploaded. An existing nonempty output directory
is refused, preserving prior evidence. The report separates:

- **Steady pitch:** only complete frames inside the guarded note interiors
  are scored. Correct MIDI, cents tolerance, octave errors, unvoiced frames,
  invalid samples, clipping, and excluded frames remain visible.
- **Pitch coverage:** correct frames divided by all eligible steady frames.
  Accuracy among voiced frames alone can look perfect while the detector
  returns no pitch for parts of the recording.
- **Accepted-event note coverage:** expected notes with at least one
  correct, usable `accepted` event during their labelled interval. A ringing
  pitch is not evidence that each repeated pick was recognized. Duplicate
  events cannot recover a note more than once.

This mode prints a baseline, not a passing certification. The normal CI checks
that the scorer handles known correct and incorrect generated audio; it does
not assert that a proprietary reference recording achieves a target accuracy.
Keep recordings, note data, and `.reference.json` annotations local. A baseline
from one processed studio recording is useful evidence, not proof of accurate
detection on other exercises or on a dry NUX input. Hold out additional
reviewed recordings when tuning the detector to avoid fitting one exercise.

## Limitations

Dalamud and other third-party tools are against FFXIV's terms of service. This
project does not provide protection from account enforcement.

Future work includes real keyboard/mouse input and polyphonic chord assessment.
These features are not part of this build.
