# FFTrueBard

Play **Final Fantasy XIV** with an electric guitar.

FFTrueBard captures your guitar signal inside the game process (WASAPI via
your NUX Mighty Space or any audio interface), detects notes with a built-in
YIN pitch detector, and maps them to in-game keyboard/mouse input.

```
Guitar ──> NUX NGA-30W (USB "Dry Out") ──> FFTrueBard (Dalamud plugin)
                                             ├─ WASAPI capture + YIN detection (built in)
                                             ├─ note -> key / mouse button mapping
                                             ├─ in-game config UI + live note log
                                             └─ /ftruebard command
```

**No Python, no extra installs.** The detector is built into the plugin.

## Install (any PC, instant updates)

1. Have XIVLauncher + Dalamud set up (naturally, since you use plugins).
2. Open `/xlsettings` in game -> **Experimental** tab -> **Custom Plugin Repositories**.
3. Add:
   ```
   https://github.com/claderino/FFTrueBard/releases/latest/download/pluginmaster.json
   ```
4. Save, open `/xlplugins`, install **FFTrueBard**.

**How updates get pushed:** every push to `main` runs CI, which builds the
plugin with a timestamp version and overwrites the `latest` release assets
(`FFTrueBard.zip` + `pluginmaster.json`). Since the version always increases,
every PC with the repo URL above pulls the new build the next time Dalamud
refreshes plugins (game restart or `/xlplugins` refresh). No manual version
bumping, no per-PC work.

## Using it

1. Set the NUX USB routing to **Dry Out** (clean signal - no amp sim, no FX).
2. `/ftruebard` in game -> pick your input device (it auto-matches "NUX").
3. Play - notes stream into the live log, mapped notes fire their actions.

Tuning knobs live in `Configuration.cs` for now (threshold, noise gate,
retrigger period, stability frames); the mapping table is there too.

## Mapping

Note name -> action, in `Configuration.cs` (in-game editing UI is on the
roadmap):

| Note | Default action |
|------|----------------|
| E2 A2 D3 G3 B3 E4 (open strings) | Mouse 1-5 (E4 spare) |
| F2 F#2 G2 G#2 ... | Keyboard keys (`Key:W`, `Key:Space`, ...) |

## Repo layout

```
FFTrueBard/            Dalamud plugin (C#, Dalamud.NET.Sdk)
  FFTrueBardPlugin.cs  entry point, event pump
  Detection/           WASAPI capture + YIN pitch detection (built in)
  Input/               input injection (currently dry-run stub w/ TODO)
  UI/                  ImGui config window (device picker, live log)
.github/workflows/     build + publish to the 'latest' release
```

## Roadmap

- [ ] Real input injection (see TODO in `Input/InputInjector.cs`)
- [ ] Note-off / key-hold semantics (hold a note to hold a key)
- [ ] In-game mapping + tuning editor
- [ ] Chat-open gating (don't type riffs into chat)
- [ ] Chord trainer / practice modes (chromagram-based)

## Status + warnings

- Dalamud API level 15 (`Dalamud.NET.Sdk/15.0.0`), .NET 10.
- Anything Dalamud-based is against FFXIV ToS in principle. Input-automation
  plugins are the category Square Enix likes least - keep this personal,
  don't submit it to the official plugin repository.
- CI requires the GitHub repository to be **public** (release asset downloads).
