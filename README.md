# Arcade launcher

A full-screen arcade front end for Windows, built to feel like walking up to a cabinet: the menu is drawn on a simulated CRT (phosphor glow, scanline beams, slot mask, curved glass, power-on/off), every game runs with a CRT shader, and every game is on free play.

- **Arcade games** in [MAME](https://www.mamedev.org), with [CRT-Guest-Advanced](https://github.com/HelelSingh/CRT-Guest-ReShade) through [ReShade](https://reshade.me) on raster games and a tuned HLSL vector look on vector games
- **Sega Dreamcast / Naomi** in [Flycast](https://github.com/flyinghead/flycast), **Sega Saturn** in [Mednafen](https://mednafen.github.io), **Atari Jaguar** in [BigPEmu](https://www.richwhitehouse.com/jaguar), all with matching CRT looks
- **Any controller**: keyboard, arcade encoders, XInput pads, DirectInput sticks and pads, trackballs. One file maps them for the menu, the hotkeys and MAME ([docs/input.md](docs/input.md))
- **Free play everywhere** (a MAME plugin): sets the game's Free Play DIP switch where it has one, otherwise makes Start drop a coin in first
- **Save states** (a MAME plugin): 10 rotating slots with screenshots and a picker; offered when a game with saves starts
- **Library scan**: reads `roms\`, asks MAME what each file is (renamed zips are recognised from their contents), checks every set and shows exactly which files are missing; grabs attract-mode previews in the background
- Menu grouping by genre, maker, decade, players or controls (genres from `catver.ini` if you add one)

No ROMs, BIOS files or games are included or downloaded. You need your own, legally obtained.

## Install

Needs Windows 10/11 (64-bit), a GPU with OpenGL 3.3 and Direct3D 9/11, and the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build the launcher.

```powershell
git clone <this repository> arcade
cd arcade
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Shortcut
```

`install.ps1` downloads the official release of each emulator into `emulators\` (checksums are checked where the publisher lists them; ReShade's signature is checked against the certificate published on reshade.me), builds the launcher into `app\`, and puts an *Arcade* shortcut on the desktop. Files already in place (including this repository's own emulator settings) are never overwritten, so it's safe to run again. `-Only mame,reshade` or `-Skip bigpemu` pick what to install; only MAME is required.

| Emulator | Version | Used for |
|---|---|---|
| MAME | 0.289 | arcade games |
| ReShade | 6.8.0 | CRT shaders for MAME and Flycast (installed as a local DLL, switched per game; nothing system-wide) |
| Flycast | 2.7 | Dreamcast, Naomi, Atomiswave |
| Mednafen | 1.32.1 | Sega Saturn |
| BigPEmu | 1.221 | Atari Jaguar |

All are stock official builds; nothing is custom-compiled. What this repository adds on top: the MAME plugins `freeplay` and `savestates`, MAME settings (`mame.ini`, HLSL presets in `ini\`, a bgfx chain), the ReShade presets, a CRT shader for Flycast/ReShade and BigPEmu, and a patched CRT-Guest-Advanced (vertical-game rotation and a ReShade 6 fix).

## Adding games

| Folder | What goes there |
|---|---|
| `roms\` | MAME ROM sets for MAME 0.289: `.zip`, `.7z` or a folder named after the set. Loose `.chd` disc images are linked to the set that needs them |
| `dreamcast\` | Dreamcast discs (`.chd`, `.gdi` + tracks, `.cdi`, `.cue`), a folder each, named after the game, e.g. `dreamcast\Some Game (USA)\Some Game (USA).chd`. Naomi / Atomiswave MAME-format zips too, with `naomi.zip` / `awbios.zip` beside them |
| `saturn\` | Saturn discs (`.cue` + `.bin`, `.chd`, `.ccd`), a folder each. Needs the BIOS in `emulators\mednafen\firmware\` (`mpr-17933.bin`, US/EU) |
| `jaguar\` | Jaguar images (`.j64`, `.jag`, `.rom`, `.abs`, `.cof`…) |

Restart the launcher to pick up new games; the boot screen shows each one being checked. Games whose files are incomplete are listed in red with the missing files.

**Menu details:** `config\games.json` overrides titles, genres, makers, years and player counts, hides games, and sets per-game Flycast options (see [config/games.example.json](config/games.example.json)). For genres across the whole MAME library, drop a [`catver.ini`](https://www.progettosnaps.net/catver/) into `config\`. Previews for console games: PNGs in `data\snap\<name>\`.

## Using it

Default controls (all changeable in `config\input.json`, see [docs/input.md](docs/input.md)):

| Menu | Pad | Keyboard |
|---|---|---|
| Move / page | D-pad, left stick; LB / RB page | arrows |
| Play | A or Start | Enter, Left Ctrl, 1 |
| Group the list | X | Left Shift |
| In-game CRT look (Guest → CRT-Geom → off) | Y | Space |
| Quit the launcher | | Escape |

| In a game | Pad | Keyboard |
|---|---|---|
| Coin / Start | Back / Start | 5 / 1 (P2: 6 / 2) |
| Buttons 1–6 | A B X Y LB RB | Left Ctrl, Left Alt, Space, Left Shift, Z, X |
| Back to the menu | hold Guide, or Back + Start, ~1 s | Escape (MAME's own) |
| Save state / load state (hold: picker) | right / left stick click | F6 / F7 |

The second pad (or keyboard R/F/D/G, A/S/Q/W/E) is player 2. Arcade encoders that send MAME's default keys work without setup.

## Free play

`emulators\mame\plugins\freeplay` runs in every MAME game:
1. A **Free Play** DIP switch or setting is switched on, if the game has one.
2. Otherwise Start is taken over: pressing it drops a coin in, then presses Start. Games without Start buttons (where a coin joins) get a coin for that player.
3. Games you've set to free play in their own operator menu (kept in their NVRAM), or that need a credit kept in RAM, are listed in `emulators\mame\plugins\freeplay\local.lua` (copy `local.example.lua`). `config\svcdrive.lua` scripts an operator menu from a step file; `config\creditfind.lua` finds a game's credit counter.

Flycast runs Naomi boards with its own free-play option. Turn it all off with `"FreePlay": false` in `config\settings.json`.

## Settings

`config\settings.json` (written with defaults on first start):

| Setting | |
|---|---|
| `GameCrt` | in-game look: `hlsl` (Guest on raster games, HLSL on vector), `geom`, `off`. Y in the menu cycles it |
| `MenuCrt`, `Crt` | the menu's CRT effect and its strengths (`Lines`, `Curvature`, `Mask`, `Halation`, `Glow`, …) |
| `FreePlay` | the free play plugin and Flycast's free play |
| `ExitHoldSeconds` | how long to hold the exit hotkey |
| `HideLightGunGames` | leave light gun games out of the menu |
| `MameExtraArgs` | extra MAME command-line options for every game |
| `Sounds` | menu sounds |

In-game CRT strengths: `emulators\mame\reshade\GuestCRT.ini` (raster MAME games), `emulators\mame\ini\vector.ini` (vector), `emulators\flycast\ArcadeCRT.ini` (Flycast), the `arcade_crt.dstack` options (BigPEmu). In MAME and Flycast, Home opens ReShade's overlay to tune it live.

Trackball, spinner and paddle games read the mouse once given an ini: `emulators\mame\ini\<set>.ini` with e.g. `mouse 1` and `trackball_device mouse` (or `dial_device` / `paddle_device`).

## Building

Source is in `launcher\` (C# / .NET 10, WinForms with hand-rolled OpenGL, no NuGet packages):

```powershell
dotnet publish launcher -c Release -o app
```

Command-line options: `--windowed`, `--run <set>`, `--select <set>`, `--input-test` (name every control you press), `--write-ctrlr` (write MAME's controller file and quit), `--shot <file.png> --shot-after <s> --quit-after-shot`, `--acts filter,right,…` (menu presses on entering the menu). Log: `app\launcher.log`. Game list cache: `data\library.json` (delete it to rescan everything).

## License

MIT for the launcher, plugins and settings ([LICENSE](LICENSE)). The patched CRT-Guest-Advanced shader stays GPL-2.0-or-later; the emulators are downloaded from their publishers under their own licenses.
