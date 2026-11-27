# Controls: `config\input.json`

Every control the launcher knows about lives in one file, `config\input.json`. It's written with the defaults the first time the launcher starts; edit it with any text editor (comments `//` are allowed) and restart the launcher.

It has four parts:

| Section | What it does |
|---|---|
| `Menu` | Moving around the menu: `Up` `Down` `Left` `Right` `Start` `Back` `Option` (game CRT look) `Filter` (grouping) `Exit` |
| `Hotkeys` | Watched while a game runs, for every emulator: `ExitGame` (hold it to go back to the menu), `SaveState`, `LoadState`, `Pause` (MAME; pressed as F6 / F7 / P, so a mouse button works even in games with MAME's mouse input off) |
| `Mame` | MAME's own controls. The launcher turns this into `emulators\mame\ctrlr\launcher.cfg` before each game |
| `StickDeadZone` | How far (0.1–0.9) a stick or analog axis must move before it counts as a direction |

Each action takes a list: any one of them works.

## Naming a control

| Binding | Device |
|---|---|
| `Key.Enter`, `Key.LControlKey`, `Key.D1`, `Key.F6`, `Key.NumPad4` | Keyboard. Names are .NET [`Keys`](https://learn.microsoft.com/dotnet/api/system.windows.forms.keys) names (`D1` = the 1 key, `LMenu` = left Alt, `Oemcomma` = comma). Arcade encoders that act as a keyboard (I-PAC, Xin-Mo in keyboard mode…) are keys too |
| `Pad.A`, `Pad2.Start` | XInput pad (Xbox-style). `Pad` = any pad, `Pad1`–`Pad4` = that one. Controls: `A B X Y LB RB LT RT Back Start Guide LS RS Up Down Left Right`, `LStickUp/Down/Left/Right`, `RStickUp/Down/Left/Right` |
| `Joy.Button1`, `Joy2.PovUp`, `Joy[VID_16C0].Button3` | Any other game controller Windows sees as a joystick: DirectInput / HID pads, fight sticks, USB arcade encoders (Ultimarc, Brook, Zero Delay…), wheels. `Joy` = any, `Joy1`–`Joy16` = that one in Windows' order, `Joy[text]` = the one whose ID contains *text* (its USB `VID_xxxx&PID_xxxx` and name, so the binding survives replugging). Controls: `Button1`–`Button32`, `PovUp/Down/Left/Right` (hat / d-pad), `Up Down Left Right` (the X/Y axes), and any axis direction `X+ X- Y+ Y- Z+ Z- R+ R- U+ U- V+ V-` |
| `Mouse.Left`, `Mouse.Right`, `Mouse.Middle`, `Mouse.X1`, `Mouse.X2` | Mouse buttons (a trackball's buttons are mouse buttons) |
| `Mame.JOYCODE_1_RZAXIS_NEG_SWITCH` | `Mame` section only: a raw MAME input code, passed through unchanged |

Hold-together combinations use ` + ` with spaces: `"Pad.Back + Pad.Start"`, `"Joy[Ultimarc].Button9 + Joy[Ultimarc].Button10"`. With `Pad.` or `Joy.` (no number), both have to be on the same controller.

**Finding a control's name:** run

```
app\ArcadeLauncher.exe --input-test
```

A console lists your pads and joysticks with their IDs, then prints the binding name of everything you press or move. Hold Escape for two seconds to quit. `app\launcher.log` also lists the controllers found at each start.

## Examples

A DirectInput arcade stick for the menu, alongside the defaults:

```json
"Menu": {
  "Up":    [ "Key.Up", "Pad.Up", "Pad.LStickUp", "Joy[VID_0079].Up" ],
  "Start": [ "Key.Enter", "Pad.A", "Joy[VID_0079].Button1" ],
  ...
}
```

Leave a game with the stick's two top buttons held together:

```json
"Hotkeys": {
  "ExitGame":  [ "Pad.Guide", "Pad.Back + Pad.Start", "Joy[VID_0079].Button9 + Joy[VID_0079].Button10" ],
  "SaveState": [ "Pad.RS" ],
  "LoadState": [ "Pad.LS" ]
}
```

A keyboard-only cabinet: an encoder sending keys doesn't need the launcher to watch anything for saves (MAME sees F6 / F7 itself), but it needs an exit key:

```json
"ExitGame": [ "Key.Escape" ]
```
(MAME also quits on Escape by itself; the hotkey works for the console emulators too.)

## MAME

`Mame.Ports` maps MAME input types to bindings. The types are MAME's own names: `P1_JOYSTICK_UP`, `P1_BUTTON1`…`P1_BUTTON10`, `START1`, `COIN1`, `P2_…`, `P3_…`, `UI_PAUSE`, `SERVICE1` and so on (MAME's *Input (general)* menu lists them; the cfg names are documented at [docs.mamedev.org](https://docs.mamedev.org/advanced/ctrlr_config.html)). An empty list (`[]`) means *nothing*; types you don't list keep MAME's defaults.

```json
"Mame": {
  "Generate": true,
  "Devices": { "JOYCODE_1": "XInput Player 1", "JOYCODE_2": "XInput Player 2" },
  "Ports": {
    "P1_BUTTON1": [ "Key.LControlKey", "Pad1.A", "Joy1.Button1" ],
    "START1":     [ "Key.D1", "Pad1.Start" ],
    "COIN1":      [ "Key.D5", "Pad1.Back" ]
  },
  "Games": {
    "<set name>": { "P1_BUTTON2": [ "Mouse.Right", "Pad1.B" ] }
  }
}
```

- **Controller numbers.** Inside `Mame`, `Pad1`/`Joy1` both mean MAME's controller 1 (`JOYCODE_1`), which isn't necessarily Windows' joystick 1. `Devices` pins MAME's numbers to devices by their MAME device ID: `XInput Player 1`…`4` for XInput pads, the product name for DirectInput ones (MAME's *Input Devices* menu shows them, as does `mame -verbose`). With XInput pads and DirectInput sticks mixed, pin them all.
- **XInput pads in MAME:** `A B X Y LB RB LS RS` are MAME buttons 1–8, `Start`/`Back` are MAME's START/SELECT, the d-pad is hat 1, the left stick X/Y axes, and `LT`/`RT` work as buttons (MAME's sliders 1/2). MAME doesn't see Guide. The right stick isn't translated: use `Mame.` codes for it (MAME's input menu shows the code when you move it).
- **Arcade sticks in XInput mode** (X-Arcade Mode 4 and the like) are two XInput pads, one per player, so `Pad1`/`Pad2` cover them. See [the X-Arcade example](#example-x-arcade-in-xinput-mode).
- **Per-game changes** go in `Games`, keyed by MAME set name. One key can list several sets, comma-separated (`"<set>, <set>"`).
- **Analog controls** (spinners, trackballs, paddles, wheels) are set per game in `emulators\mame\ini\<set>.ini` with MAME's own options (`mouse 1`, `trackball_device mouse`, `dial_device mouse`…), not here.
- `"Generate": false` leaves MAME's controls entirely to you: the launcher stops writing `ctrlr\launcher.cfg`, and `mame.ini`'s `ctrlr` setting applies.
- `app\ArcadeLauncher.exe --write-ctrlr` writes the file without starting anything, so you can look at it.

## Example: X-Arcade in XInput mode

With the mode switch on 4, an X-Arcade (Tri-Mode board) is two XInput pads, left side = `Pad1`, right side = `Pad2`. Each side sends:

| Button | Sends | MAME |
|---|---|---|
| Stick | `Up Down Left Right` (d-pad) | joystick |
| Middle row 1 2 3 | `A B RB` | buttons 1 2 3 |
| Top row 1 2 3 | `X Y LB` | buttons 4 5 6 |
| Bottom row 1 2 | `LT RT` | buttons 7 8 |
| Player 1 / Player 2 | `Start` | start |
| Pinball button (side) | `Back` | coin |

The trackball is a mouse: its left and right buttons are P1 buttons 1 and 2 (in games that turn MAME's mouse input on), and the red button is the middle button, used here as pause. No Guide button and no stick clicks, so the other hotkeys use combinations: hold the pinball button + Start to leave a game, pinball button + bottom-row 2 to save, pinball button + bottom-row 1 to load (hold for the picker).

```json
"Hotkeys": {
  "ExitGame":  [ "Pad.Guide", "Pad.Back + Pad.Start" ],
  "SaveState": [ "Pad.RS", "Pad.Back + Pad.RT" ],
  "LoadState": [ "Pad.LS", "Pad.Back + Pad.LT" ],
  "Pause":     [ "Mouse.Middle" ]
},
"Mame": {
  "Ports": {
    "P1_BUTTON1": [ "Key.LControlKey", "Mouse.Left", "Pad1.A" ],
    "P1_BUTTON2": [ "Key.LMenu", "Mouse.Right", "Pad1.B" ],
    "P1_BUTTON3": [ "Key.Space", "Pad1.RB" ],
    "P1_BUTTON4": [ "Key.LShiftKey", "Pad1.X" ],
    "P1_BUTTON5": [ "Key.Z", "Pad1.Y" ],
    "P1_BUTTON6": [ "Key.X", "Pad1.LB" ],
    "P1_BUTTON7": [ "Key.C", "Pad1.LT" ],
    "P1_BUTTON8": [ "Pad1.RT" ],
    ...the same for P2 with Pad2
  },
  "Games": {
    // 4-button games: middle 1 2, top 1 2
    "<set>, <set>, ...": {
      "P1_BUTTON3": [ "Key.Space", "Pad1.X" ],
      "P1_BUTTON4": [ "Key.LShiftKey", "Pad1.Y" ],
      ...the same for P2
    },
    // 6-button fighters: punches on the top row, kicks on the middle row
    "<set>, <set>, ...": {
      "P1_BUTTON1": [ "Key.LControlKey", "Pad1.X" ],
      "P1_BUTTON2": [ "Key.LMenu", "Pad1.Y" ],
      "P1_BUTTON3": [ "Key.Space", "Pad1.LB" ],
      "P1_BUTTON4": [ "Key.LShiftKey", "Pad1.A" ],
      "P1_BUTTON5": [ "Key.Z", "Pad1.B" ],
      "P1_BUTTON6": [ "Key.X", "Pad1.RB" ],
      ...the same for P2
    }
  }
}
```

## Example: players 3 and 4 on a Bliss-Box

A Bliss-Box 4-Play in HID mode is four DirectInput controllers, one per port, named `Gamer-Pro Advanced.1` to `.4` (USB `VID_16D0`, PID `0D04`–`0D07`). Plug it straight into the PC: it doesn't work through a USB hub. With a 4-button pad the stick is the hat, the buttons are `Button1`–`Button4`, Select is `Button5` and Start is `Button6`. Pin the ports to MAME controller numbers that are still free, then map them:

```json
"Devices": { ..., "JOYCODE_4": "Gamer-Pro Advanced.1", "JOYCODE_5": "Gamer-Pro Advanced.2" },
"Ports": {
  "P3_JOYSTICK_UP": [ "Key.I", "Joy4.PovUp" ],
  ...
  "P3_BUTTON1": [ "Key.RControlKey", "Joy4.Button1" ],
  ...
  "START3": [ "Key.D3", "Joy4.Button6" ],
  "COIN3":  [ "Key.D7", "Joy4.Button5" ],
  ...the same for P4 with Joy5
}
```

MAME's *Input (this machine)* menu shows a game's button count (as do the `<control buttons=...>` entries of `mame -listxml`).

## Other emulators

Flycast, Mednafen and BigPEmu keep their own control mappings; the launcher's `Hotkeys.ExitGame` works on top of all of them.

| Emulator | Where controls are set |
|---|---|
| Flycast (Dreamcast / Naomi) | Maps XInput pads by itself. Other controllers: Settings → Controls in Flycast (Tab during a game); saved in `emulators\flycast\mappings\` |
| Mednafen (Saturn) | Keyboard by default (arrows, Enter = Start, J/K/L = A/B/C). Map a controller in game with Alt+Shift+1 (port 1); saved in `emulators\mednafen\mednafen.cfg` |
| BigPEmu (Jaguar) | Input → Auto-Assign Devices handles known pads; anything else in BigPEmu's Input menu; saved in `emulators\bigpemu\UserData\` |
