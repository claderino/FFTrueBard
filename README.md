# FFTrueBard

Play **Final Fantasy XIV** with an electric guitar.

A guitar note detector streams note events to a Dalamud plugin, which maps them
to in-game keyboard/mouse input. Your NUX Mighty Space (NGA-30W) acts as the
audio interface (USB routing set to **Dry Out**).

```
Guitar ──> NUX NGA-30W (USB "Dry Out") ──> detector.py (YIN pitch detection)
                                               │  JSON lines, 127.0.0.1:4242
                                               ▼
                                  FFTrueBard (Dalamud plugin)
                                   ├─ maps notes -> keys / mouse buttons
                                   ├─ in-game config UI + live note log
                                   └─ /ftruebard command
```

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

> Note: local dev builds (version `1.0.0`) will always be stomped by CI builds.
> That's intentional - CI is the source of truth. For quick local iteration,
> drop the DLL in `%APPDATA%\XIVLauncher\devPlugins\` and expect it to be
> replaced by CI builds on refresh.

## Run the detector

```powershell
cd tools\detector
python -m venv .venv && .venv\Scripts\activate
pip install -r requirements.txt
python detector.py --device NUX
```

- `--list-devices` to find your input device.
- Set the NUX USB routing to **Dry Out** (clean signal - no amp sim, no FX).
- Notes appear in the plugin window (`/ftruebard`) and are injected per the
  mapping table (see `FFTrueBard/Configuration.cs`).

## Mapping

Note name -> action, editable in `Configuration.cs` (in-game editing UI is on
the roadmap):

| Note | Default action |
|------|----------------|
| E2 A2 D3 G3 B3 E4 (open strings) | Mouse 1-5 (E4 spare) |
| F2 F#2 G2 G#2 ... | Keyboard keys (`Key:W`, `Key:Space`, ...) |

## Repo layout

```
FFTrueBard/            Dalamud plugin (C#, Dalamud.NET.Sdk)
  FFTrueBardPlugin.cs  entry point, event pump
  Link/                localhost TCP server receiving note events
  Input/               input injection (currently dry-run stub w/ TODO)
  UI/                  ImGui config window
tools/detector/        Python note detector (YIN, WASAPI capture)
.github/workflows/     build + publish to the 'latest' release
```

## Roadmap

- [ ] Real input injection (see TODO in `Input/InputInjector.cs`)
- [ ] Note-off / key-hold semantics (hold a note to hold a key)
- [ ] In-game mapping editor
- [ ] Chat-open gating (don't type riffs into chat)
- [ ] Chord trainer / practice modes (chromagram-based)

## Status + warnings

- Dalamud API level 15 (`Dalamud.NET.Sdk/15.0.0`).
- Anything Dalamud-based is against FFXIV ToS in principle. Input-automation
  plugins are the category Square Enix likes least - keep this personal,
  don't submit it to the official plugin repository.
- CI requires the GitHub repository to be **public** (release asset downloads).
