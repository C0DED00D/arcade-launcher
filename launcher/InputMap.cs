using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcadeLauncher;

// ------------------------------------------------------------------------------------------------------------------
// Configurable input (config\input.json).
//
// A binding is a string naming one control on one device:
//   Key.<name>          keyboard key (System.Windows.Forms.Keys name: Enter, LControlKey, D1, F6, NumPad4 ...)
//   Pad.<control>       any XInput pad          Pad1 .. Pad4 = that pad only
//                       A B X Y LB RB LT RT Back Start Guide LS RS Up Down Left Right
//                       LStickUp LStickDown LStickLeft LStickRight RStickUp RStickDown RStickLeft RStickRight
//   Joy.<control>       any DirectInput / HID game controller (arcade encoders, fight sticks, wheels, old pads)
//                       Joy1 .. Joy16 = that one (Windows' joystick order),  Joy[text] = the one whose
//                       "VID_xxxx&PID_xxxx name" contains text, e.g. Joy[VID_D209] or Joy[Ultimarc]
//                       Button1 .. Button32  PovUp PovDown PovLeft PovRight  Up Down Left Right (X/Y axes)
//                       X+ X- Y+ Y- Z+ Z- R+ R- U+ U- V+ V-
//   Mouse.<button>      Left Right Middle X1 X2 (a trackball's buttons are mouse buttons)
//   Mame.<token>        MAME only: passed straight through, e.g. Mame.JOYCODE_3_BUTTON7
// Join controls with " + " for a combination that must be held together: "Pad.Back + Pad.Start".
// A combination of controls on the same "any" device (Pad.*, Joy.*) has to be held on one device.
// Run  ArcadeLauncher.exe --input-test  and press things to see their binding names.
// ------------------------------------------------------------------------------------------------------------------

/// <summary>One control on one device.</summary>
sealed record InputAtom(string Source, int? Index, string? Match, string Control, string Text)
{
    static readonly Regex Rx = new(@"^(?<src>[A-Za-z]+)(?:(?<idx>\d+)|\[(?<match>[^\]]+)\])?\.(?<ctl>.+)$");

    public static InputAtom? Parse(string s)
    {
        var m = Rx.Match(s.Trim());
        if (!m.Success) return null;
        string src = m.Groups["src"].Value.ToLowerInvariant();
        if (src is not ("key" or "pad" or "joy" or "mouse" or "mame")) return null;
        int? idx = m.Groups["idx"].Success ? int.Parse(m.Groups["idx"].Value) : null;
        string? match = m.Groups["match"].Success ? m.Groups["match"].Value : null;
        return new InputAtom(src, idx, match, m.Groups["ctl"].Value.Trim(), s.Trim());
    }
}

/// <summary>A combination of controls held together ("Pad.Back + Pad.Start"), or just one.</summary>
sealed class InputBinding
{
    public readonly List<InputAtom> Atoms = new();
    public readonly string Text;
    InputBinding(string text) { Text = text; }

    public static InputBinding? Parse(string s, string where)
    {
        var b = new InputBinding(s.Trim());
        foreach (var part in SplitCombo(s))
        {
            var a = InputAtom.Parse(part);
            if (a == null) { Log.Write($"input.json {where}: \"{s}\" isn't a binding (expected e.g. Key.Enter, Pad.A, Joy2.Button3)"); return null; }
            b.Atoms.Add(a);
        }
        if (b.Atoms.Count == 0) return null;
        foreach (var a in b.Atoms)
            if (!InputDevices.Knows(a)) Log.Write($"input.json {where}: \"{a.Text}\" - unknown control \"{a.Control}\" for {a.Source}, ignored");
        return b;
    }

    /// <summary>True while every control in the binding is held. keys = how keyboard keys are read (null: skip them).</summary>
    public bool IsDown(Func<Keys, bool>? keys)
    {
        // Pad.Back + Pad.Start: both on the same pad (any one), not Back on one pad and Start on another
        var first = Atoms[0];
        if (Atoms.Count > 1 && first.Source is "pad" or "joy" && first.Index == null && first.Match == null
            && Atoms.All(a => a.Source == first.Source && a.Index == null && a.Match == null))
        {
            int n = first.Source == "pad" ? 4 : InputDevices.JoyCount;
            for (int i = 1; i <= n; i++)
                if (Atoms.All(a => InputDevices.Down(a with { Index = i }, keys))) return true;
            return false;
        }
        return Atoms.All(a => InputDevices.Down(a, keys));
    }

    public bool UsesKeyboard => Atoms.Any(a => a.Source == "key");

    /// <summary>"Pad.Back + Pad.Start" -> its parts. The '+' needs a space each side, so an axis like "Joy.X+" stays whole.</summary>
    public static string[] SplitCombo(string s) => Regex.Split(s.Trim(), @"\s+\+\s+").Where(p => p.Trim() != "").ToArray();
}

/// <summary>Any of several bindings.</summary>
sealed class InputBindings
{
    public readonly List<InputBinding> List = new();
    public static InputBindings From(IEnumerable<string>? items, string where)
    {
        var r = new InputBindings();
        foreach (var s in items ?? Enumerable.Empty<string>())
            if (InputBinding.Parse(s, where) is InputBinding b) r.List.Add(b);
        return r;
    }
    public bool IsDown(Func<Keys, bool>? keys) => List.Any(b => (keys != null || !b.UsesKeyboard) && b.IsDown(keys));
}

/// <summary>Reads XInput pads, DirectInput/HID joysticks (through winmm), the mouse buttons and the keyboard.</summary>
static class InputDevices
{
    public static double StickDeadZone = 0.4;     // fraction of full travel before a stick/axis counts as a direction
    static readonly XInput.Gamepad?[] _pads = new XInput.Gamepad?[4];
    static readonly Joy[] _joys = new Joy[16];
    static long _nextJoyScan;

    sealed class Joy
    {
        public bool Present; public long NextProbe; public JoyCaps Caps; public string Id = ""; public Winmm.JOYINFOEX State;
        public double Axis(int i)
        {
            (uint v, uint lo, uint hi) = i switch
            {
                0 => ((uint)State.dwXpos, Caps.wXmin, Caps.wXmax), 1 => ((uint)State.dwYpos, Caps.wYmin, Caps.wYmax),
                2 => ((uint)State.dwZpos, Caps.wZmin, Caps.wZmax), 3 => ((uint)State.dwRpos, Caps.wRmin, Caps.wRmax),
                4 => ((uint)State.dwUpos, Caps.wUmin, Caps.wUmax), _ => ((uint)State.dwVpos, Caps.wVmin, Caps.wVmax),
            };
            if (hi <= lo) return 0;
            return (v - lo) / (double)(hi - lo) * 2 - 1;  // -1 .. 1
        }
        public bool HasAxis(int i) => i < 2 || (Caps.wCaps & (i switch { 2 => 1u, 3 => 2u, 4 => 4u, _ => 8u })) != 0;
        // JOYCAPS_HASPOV: a controller without a hat (e.g. an Arduino joystick) reports dwPOV = 0, which would read as "up"
        public bool HasPov => (Caps.wCaps & 0x10) != 0;
    }
    struct JoyCaps { public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wNumButtons; }

    public static int JoyCount => 16;

    /// <summary>Reads every device once (call once a frame before checking bindings).</summary>
    public static void Poll()
    {
        for (int i = 0; i < 4; i++) _pads[i] = XInput.TryGet(i, out var p) ? p : null;
        long now = Environment.TickCount64;
        bool rescan = now >= _nextJoyScan;
        if (rescan) _nextJoyScan = now + 3000;
        for (int i = 0; i < 16; i++)
        {
            var j = _joys[i] ??= new Joy();
            if (!j.Present && (!rescan || now < j.NextProbe)) continue;
            var info = new Winmm.JOYINFOEX { dwSize = Marshal.SizeOf<Winmm.JOYINFOEX>(), dwFlags = Winmm.JOY_RETURNALL };
            if (Winmm.joyGetPosEx((uint)i, ref info) != 0)
            {
                if (j.Present) Log.Write($"input: joystick {i + 1} gone ({j.Id})");
                j.Present = false; j.NextProbe = now + 3000;
                continue;
            }
            if (!j.Present) Connect(i, j);
            j.State = info;
        }
    }

    static void Connect(int i, Joy j)
    {
        var caps = new Winmm.JOYCAPSW();
        if (Winmm.joyGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<Winmm.JOYCAPSW>()) != 0) return;
        j.Caps = new JoyCaps
        {
            wXmin = caps.wXmin, wXmax = caps.wXmax, wYmin = caps.wYmin, wYmax = caps.wYmax, wZmin = caps.wZmin, wZmax = caps.wZmax,
            wRmin = caps.wRmin, wRmax = caps.wRmax, wUmin = caps.wUmin, wUmax = caps.wUmax, wVmin = caps.wVmin, wVmax = caps.wVmax,
            wCaps = caps.wCaps, wNumButtons = caps.wNumButtons,
        };
        string vidpid = $"VID_{caps.wMid:X4}&PID_{caps.wPid:X4}";
        string name = OemName(vidpid) ?? caps.szPname;
        j.Id = $"{vidpid} {name}";
        j.Present = true;
        Log.Write($"input: joystick {i + 1} = {j.Id}, {caps.wNumButtons} buttons, {caps.wNumAxes} axes");
    }

    static string? OemName(string vidpid)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" + vidpid);
                if (k?.GetValue("OEMName") is string s && s.Trim() != "") return s.Trim();
            }
            catch { }
        }
        return null;
    }

    public static IEnumerable<(int Number, string Id)> Joysticks()
    {
        for (int i = 0; i < 16; i++) if (_joys[i] is { Present: true } j) yield return (i + 1, j.Id);
    }
    public static IEnumerable<int> Pads()
    {
        for (int i = 0; i < 4; i++) if (_pads[i] != null) yield return i + 1;
    }

    static readonly Dictionary<string, ushort> PadButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = XInput.A, ["B"] = XInput.B, ["X"] = XInput.X, ["Y"] = XInput.Y, ["LB"] = XInput.LB, ["RB"] = XInput.RB,
        ["Back"] = XInput.Back, ["Start"] = XInput.Start, ["Guide"] = XInput.Guide, ["LS"] = XInput.LThumb, ["RS"] = XInput.RThumb,
        ["Up"] = XInput.Up, ["Down"] = XInput.Down, ["Left"] = XInput.Left, ["Right"] = XInput.Right,
    };
    static readonly string[] PadAnalog = { "LT", "RT", "LStickUp", "LStickDown", "LStickLeft", "LStickRight", "RStickUp", "RStickDown", "RStickLeft", "RStickRight" };
    static readonly string[] JoyNamed = { "PovUp", "PovDown", "PovLeft", "PovRight", "Up", "Down", "Left", "Right" };
    static readonly Dictionary<string, int> MouseVk = new(StringComparer.OrdinalIgnoreCase) { ["Left"] = 1, ["Right"] = 2, ["Middle"] = 4, ["X1"] = 5, ["X2"] = 6 };

    public static bool Knows(InputAtom a) => a.Source switch
    {
        "key" => Enum.TryParse<Keys>(a.Control, true, out _),
        "pad" => PadButtons.ContainsKey(a.Control) || PadAnalog.Contains(a.Control, StringComparer.OrdinalIgnoreCase),
        "joy" => JoyNamed.Contains(a.Control, StringComparer.OrdinalIgnoreCase) || JoyButton(a.Control) > 0 || JoyAxis(a.Control) >= 0,
        "mouse" => MouseVk.ContainsKey(a.Control),
        "mame" => true,
        _ => false,
    };

    static int JoyButton(string c) => c.StartsWith("Button", StringComparison.OrdinalIgnoreCase) && int.TryParse(c[6..], out int n) && n is >= 1 and <= 32 ? n : 0;
    static int JoyAxis(string c) => c.Length == 2 && (c[1] == '+' || c[1] == '-') ? "XYZRUV".IndexOf(char.ToUpperInvariant(c[0])) : -1;

    public static bool Down(InputAtom a, Func<Keys, bool>? keys)
    {
        switch (a.Source)
        {
            case "key":
                return keys != null && Enum.TryParse<Keys>(a.Control, true, out var k) && keys(k);
            case "mouse":
                return MouseVk.TryGetValue(a.Control, out int vk) && (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
            case "pad":
                if (a.Index is int pi) return pi is >= 1 and <= 4 && _pads[pi - 1] is XInput.Gamepad p1 && PadDown(p1, a.Control);
                foreach (var p in _pads) if (p is XInput.Gamepad pp && PadDown(pp, a.Control)) return true;
                return false;
            case "joy":
                for (int i = 0; i < 16; i++)
                {
                    var j = _joys[i];
                    if (j is not { Present: true }) continue;
                    if (a.Index is int ji && ji != i + 1) continue;
                    if (a.Match != null && !j.Id.Contains(a.Match, StringComparison.OrdinalIgnoreCase)) continue;
                    if (JoyDown(j, a.Control)) return true;
                }
                return false;
        }
        return false; // Mame.* only means something to MAME
    }

    static bool PadDown(XInput.Gamepad p, string c)
    {
        if (PadButtons.TryGetValue(c, out var bit)) return (p.Buttons & bit) != 0;
        int dz = (int)(StickDeadZone * 32767);
        return c.ToLowerInvariant() switch
        {
            "lt" => p.LT > 128, "rt" => p.RT > 128,
            "lstickup" => p.LY > dz, "lstickdown" => p.LY < -dz, "lstickleft" => p.LX < -dz, "lstickright" => p.LX > dz,
            "rstickup" => p.RY > dz, "rstickdown" => p.RY < -dz, "rstickleft" => p.RX < -dz, "rstickright" => p.RX > dz,
            _ => false,
        };
    }

    static bool JoyDown(Joy j, string c)
    {
        int b = JoyButton(c);
        if (b > 0) return (j.State.dwButtons & (1 << (b - 1))) != 0;
        int ax = JoyAxis(c);
        if (ax >= 0)
        {
            if (!j.HasAxis(ax)) return false;
            double v = j.Axis(ax);
            return c[1] == '+' ? v > StickDeadZone : v < -StickDeadZone;
        }
        int pov = j.State.dwPOV;  // hundredths of a degree clockwise from up, 0xFFFF = centred
        bool centred = !j.HasPov || pov < 0 || pov >= 36000;
        switch (c.ToLowerInvariant())
        {
            case "povup": return !centred && (pov >= 31500 || pov <= 4500);
            case "povright": return !centred && pov >= 4500 && pov <= 13500;
            case "povdown": return !centred && pov >= 13500 && pov <= 22500;
            case "povleft": return !centred && pov >= 22500 && pov <= 31500;
            case "up": return j.Axis(1) < -StickDeadZone;
            case "down": return j.Axis(1) > StickDeadZone;
            case "left": return j.Axis(0) < -StickDeadZone;
            case "right": return j.Axis(0) > StickDeadZone;
        }
        return false;
    }

    /// <summary>Every control held right now, as binding names (for --input-test).</summary>
    public static List<string> HeldNow()
    {
        var list = new List<string>();
        for (int vk = 8; vk < 255; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12) continue; // generic Shift/Ctrl/Alt: the left/right ones are listed
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0) list.Add("Key." + (Keys)vk);
        }
        foreach (var (name, vk) in MouseVk) if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0) list.Add("Mouse." + name);
        for (int i = 0; i < 4; i++)
        {
            if (_pads[i] is not XInput.Gamepad p) continue;
            foreach (var c in PadButtons.Keys.Concat(PadAnalog)) if (PadDown(p, c)) list.Add($"Pad{i + 1}.{c}");
        }
        for (int i = 0; i < 16; i++)
        {
            if (_joys[i] is not { Present: true } j) continue;
            for (int b = 1; b <= 32; b++) if ((j.State.dwButtons & (1 << (b - 1))) != 0) list.Add($"Joy{i + 1}.Button{b}");
            foreach (var c in new[] { "PovUp", "PovDown", "PovLeft", "PovRight" }) if (JoyDown(j, c)) list.Add($"Joy{i + 1}.{c}");
            for (int ax = 0; ax < 6; ax++)
            {
                if (!j.HasAxis(ax)) continue;
                double v = j.Axis(ax);
                if (Math.Abs(v) > StickDeadZone) list.Add($"Joy{i + 1}.{"XYZRUV"[ax]}{(v > 0 ? '+' : '-')}");
            }
        }
        return list;
    }
}

static class Winmm
{
    public const int JOY_RETURNALL = 0xFF;
    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX
    {
        public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax, wRmin, wRmax, wUmin, wUmax, wVmin, wVmax,
            wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }
    [DllImport("winmm.dll")] public static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint joyGetDevCapsW(UIntPtr id, ref JOYCAPSW caps, uint size);
}

/// <summary>config\input.json: what every control does, in the menu, while a game runs, and inside MAME.</summary>
public class InputConfig
{
    /// <summary>Fraction of full travel before a stick or axis counts as pushed (0.1 .. 0.9).</summary>
    public double StickDeadZone { get; set; } = 0.4;

    /// <summary>Menu actions: Up Down Left Right Start Back Option Filter Exit.</summary>
    public Dictionary<string, List<string>> Menu { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Up"] = new() { "Key.Up", "Key.R", "Pad.Up", "Pad.LStickUp", "Joy.PovUp", "Joy.Up" },
        ["Down"] = new() { "Key.Down", "Key.F", "Pad.Down", "Pad.LStickDown", "Joy.PovDown", "Joy.Down" },
        ["Left"] = new() { "Key.Left", "Key.D", "Pad.Left", "Pad.LStickLeft", "Pad.LB", "Joy.PovLeft", "Joy.Left" },
        ["Right"] = new() { "Key.Right", "Key.G", "Pad.Right", "Pad.LStickRight", "Pad.RB", "Joy.PovRight", "Joy.Right" },
        ["Start"] = new() { "Key.Enter", "Key.ControlKey", "Key.D1", "Key.D2", "Key.A", "Pad.A", "Pad.Start", "Joy.Button1" },
        ["Back"] = new() { "Key.Menu", "Key.S", "Key.Back", "Pad.B", "Joy.Button2" },
        ["Option"] = new() { "Key.Space", "Key.Q", "Key.Tab", "Pad.Y", "Joy.Button4" },
        ["Filter"] = new() { "Key.ShiftKey", "Key.W", "Pad.X", "Joy.Button3" },
        ["Exit"] = new() { "Key.Escape" },
    };

    /// <summary>
    /// While a game runs (the launcher watches these in the background, for every emulator):
    /// ExitGame (hold for Settings.ExitHoldSeconds) - back to the menu, closing the emulator properly.
    /// SaveState / LoadState (MAME) - pressed as F6 / F7 for the save-state plugin; hold LoadState for the picker.
    /// Pause (MAME) - pressed as P (MAME's pause key); none by default.
    /// PauseSwitch (MAME) - an on/off switch: turning it on pauses, turning it off resumes; none by default.
    /// Keyboard keys aren't watched for SaveState/LoadState/Pause (press F6/F7/P themselves).
    /// </summary>
    public Dictionary<string, List<string>> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ExitGame"] = new() { "Pad.Guide", "Pad.Back + Pad.Start" },
        ["SaveState"] = new() { "Pad.RS" },
        ["LoadState"] = new() { "Pad.LS" },
        ["Pause"] = new(),
        ["PauseSwitch"] = new(),
    };

    public MameInput Mame { get; set; } = new();

    public class MameInput
    {
        /// <summary>Write emulators\mame\ctrlr\launcher.cfg from these bindings at every launch and start MAME with it.</summary>
        public bool Generate { get; set; } = true;

        /// <summary>
        /// Pin MAME's controller numbers to devices (MAME's own device IDs: "XInput Player 1", a DirectInput product
        /// name, or part of a HID path such as "VID_D209&amp;PID_1601"). MAME's "Input Devices" menu shows them.
        /// </summary>
        public Dictionary<string, string> Devices { get; set; } = new()
        {
            ["JOYCODE_1"] = "XInput Player 1",
            ["JOYCODE_2"] = "XInput Player 2",
        };

        /// <summary>
        /// MAME input types (P1_BUTTON1, START1, COIN2, P1_JOYSTICK_UP, UI_PAUSE ...) and what presses them. An empty
        /// list = nothing. Types not listed keep MAME's defaults. In here, PadN / JoyN mean MAME's controller N
        /// (JOYCODE_N), which can differ from Windows' order: pin it with Devices.
        /// </summary>
        public Dictionary<string, List<string>> Ports { get; set; } = DefaultPorts();

        /// <summary>
    /// Per-game changes on top of Ports: { "&lt;MAME set name&gt;": { "P1_BUTTON2": [ ... ] } }. A key can list several
    /// sets separated by commas ("sf2, sfa3"); MAME applies a parent's entry to its clones.
    /// </summary>
        public Dictionary<string, Dictionary<string, List<string>>> Games { get; set; } = new();

        /// <summary>
        /// Sets (or parents) that save a state when left with the ExitGame hotkey, so the next start offers to carry on
        /// (the save-state picker opens at launch when a game has saves). "*" = every MAME game: plugins\savestates
        /// itself refuses games whose drivers can't save (unless its local.lua allows them), so nothing bad is written.
        /// </summary>
        public List<string> SaveOnExit { get; set; } = new() { "*" };

        /// <summary>
        /// Port changes for every game with at most FewButtonsMax buttons (MAME's own count), on top of Ports. A Games
        /// entry for the set or its parent still wins, port by port. Applied to the game being launched only.
        /// </summary>
        public Dictionary<string, List<string>> FewButtons { get; set; } = new();
        public int FewButtonsMax { get; set; } = 4;
    }

    static Dictionary<string, List<string>> DefaultPorts()
    {
        var d = new Dictionary<string, List<string>>();
        // P1: arrows, Left Ctrl / Alt / Space / Shift / Z / X (MAME's own keys, which I-PAC style encoders send) + pad 1
        // P2: R F D G, A S Q W E (MAME's own) + pad 2
        string[][] keys =
        {
            new[] { "Up", "Down", "Left", "Right", "LControlKey", "LMenu", "Space", "LShiftKey", "Z", "X", "D1", "D5" },
            new[] { "R", "F", "D", "G", "A", "S", "Q", "W", "E", "", "D2", "D6" },
        };
        string[] mouse = { "Left", "Middle", "Right" };
        for (int p = 1; p <= 2; p++)
        {
            var k = keys[p - 1];
            string pad = "Pad" + p;
            d[$"P{p}_JOYSTICK_UP"] = new() { "Key." + k[0], pad + ".Up", pad + ".LStickUp" };
            d[$"P{p}_JOYSTICK_DOWN"] = new() { "Key." + k[1], pad + ".Down", pad + ".LStickDown" };
            d[$"P{p}_JOYSTICK_LEFT"] = new() { "Key." + k[2], pad + ".Left", pad + ".LStickLeft" };
            d[$"P{p}_JOYSTICK_RIGHT"] = new() { "Key." + k[3], pad + ".Right", pad + ".LStickRight" };
            string[] padButtons = { "A", "B", "X", "Y", "LB", "RB" };
            for (int b = 1; b <= 6; b++)
            {
                var list = new List<string>();
                if (k[3 + b] != "") list.Add("Key." + k[3 + b]);
                if (p == 1 && b <= 3) list.Add("Mouse." + mouse[b - 1]);
                list.Add(pad + "." + padButtons[b - 1]);
                d[$"P{p}_BUTTON{b}"] = list;
            }
            d[$"START{p}"] = new() { "Key." + k[10], pad + ".Start" };
            d[$"COIN{p}"] = new() { "Key." + k[11], pad + ".Back" };
        }
        // F6 / F7 belong to the savestates plugin (rotating slots + picker): MAME's own slot prompts are switched off
        foreach (var t in new[] { "UI_SAVE_STATE", "UI_LOAD_STATE", "UI_SAVE_STATE_QUICK", "UI_LOAD_STATE_QUICK" }) d[t] = new();
        return d;
    }

    // ---- loaded form ----
    internal Dictionary<Act, InputBindings> MenuBindings { get; private set; } = new();
    internal InputBindings ExitGame { get; private set; } = new();
    internal InputBindings SaveState { get; private set; } = new();
    internal InputBindings LoadState { get; private set; } = new();
    internal InputBindings Pause { get; private set; } = new();
    internal InputBindings PauseSwitch { get; private set; } = new();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Loads config\input.json, or writes the defaults there (taking over an older settings.json "Keys").</summary>
    public static InputConfig Load(string path, Settings settings)
    {
        InputConfig? c = null;
        try { if (File.Exists(path)) c = JsonSerializer.Deserialize<InputConfig>(File.ReadAllText(path), Opts); }
        catch (Exception ex) { Log.Write("input.json unreadable, using the defaults: " + ex.Message); }
        if (c == null)
        {
            c = new InputConfig();
            if (settings.Keys is { Count: > 0 } old)
            {
                foreach (var (name, keys) in old)
                {
                    if (!c.Menu.TryGetValue(name, out var list)) continue;
                    list.RemoveAll(s => s.StartsWith("Key.", StringComparison.OrdinalIgnoreCase));
                    list.InsertRange(0, keys.Select(k => "Key." + k));
                }
                Log.Write("input.json created from settings.json \"Keys\"");
            }
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, JsonSerializer.Serialize(c, Opts)); } catch (Exception ex) { Log.Write("input.json not written: " + ex.Message); }
            }
        }
        settings.Keys = null; // input.json has them now
        // a newer launcher's menu actions get their defaults in an older input.json
        var menu = new Dictionary<string, List<string>>(c.Menu, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in new InputConfig().Menu) menu.TryAdd(k, v);
        c.Menu = menu;
        c.Hotkeys = new Dictionary<string, List<string>>(c.Hotkeys, StringComparer.OrdinalIgnoreCase);
        c.Prepare();
        return c;
    }

    /// <summary>
    /// A short on-screen name for what does an action: the first binding on the kind of controller that's plugged in
    /// (XInput pad, else other controller, else keyboard). "Pad.Back + Pad.Start" -> "Back + Start", "Joy.Button3" -> "Button 3".
    /// </summary>
    public string Label(string section, string name)
    {
        var dict = section switch { "Menu" => Menu, "Hotkeys" => Hotkeys, _ => Mame.Ports };
        if (!dict.TryGetValue(name, out var list) || list.Count == 0) return name;
        string want = InputDevices.Pads().Any() ? "pad" : InputDevices.Joysticks().Any() ? "joy" : "key";
        var pick = list.FirstOrDefault(b => InputBinding.SplitCombo(b).All(p => InputAtom.Parse(p)?.Source == want)) ?? list[0];
        return string.Join(" + ", InputBinding.SplitCombo(pick).Select(p =>
        {
            var a = InputAtom.Parse(p);
            if (a == null) return p;
            string c = a.Control;
            if (a.Source == "key")
                c = c switch { "D0" or "D1" or "D2" or "D3" or "D4" or "D5" or "D6" or "D7" or "D8" or "D9" => c[1..], "ControlKey" or "LControlKey" => "Ctrl", "ShiftKey" or "LShiftKey" => "Shift", "Menu" or "LMenu" => "Alt", _ => c };
            c = Regex.Replace(c, @"^Button(\d+)$", "Button $1");
            return c;
        }));
    }

    void Prepare()
    {
        InputDevices.StickDeadZone = Math.Clamp(StickDeadZone, 0.1, 0.9);
        MenuBindings = new();
        foreach (var (name, list) in Menu)
        {
            if (Enum.TryParse<Act>(name, true, out var act)) MenuBindings[act] = InputBindings.From(list, "Menu." + name);
            else Log.Write($"input.json: Menu.{name} isn't a menu action ({string.Join(", ", Enum.GetNames<Act>())})");
        }
        ExitGame = InputBindings.From(Hotkeys.GetValueOrDefault("ExitGame"), "Hotkeys.ExitGame");
        SaveState = InputBindings.From(Hotkeys.GetValueOrDefault("SaveState"), "Hotkeys.SaveState");
        LoadState = InputBindings.From(Hotkeys.GetValueOrDefault("LoadState"), "Hotkeys.LoadState");
        Pause = InputBindings.From(Hotkeys.GetValueOrDefault("Pause"), "Hotkeys.Pause");
        PauseSwitch = InputBindings.From(Hotkeys.GetValueOrDefault("PauseSwitch"), "Hotkeys.PauseSwitch");
        if (ExitGame.List.Count == 0) Log.Write("input.json: no Hotkeys.ExitGame binding - games can only be left from the emulator itself");
    }
}

/// <summary>Turns input.json's Mame section into a MAME controller file (ctrlr\launcher.cfg).</summary>
static class MameCtrlr
{
    public const string Name = "launcher";

    /// <summary>
    /// The port bindings a game ends up with (Ports, then FewButtons if it qualifies, then Games entries for its parent
    /// and itself), as the launcher reads them (CharSelect uses each player's).
    /// </summary>
    public static Dictionary<string, List<string>> EffectivePorts(InputConfig cfg, GameInfo g)
    {
        var d = new Dictionary<string, List<string>>(cfg.Mame.Ports, StringComparer.OrdinalIgnoreCase);
        if (g.Buttons > 0 && g.Buttons <= cfg.Mame.FewButtonsMax) foreach (var (k, v) in cfg.Mame.FewButtons) d[k] = v;
        foreach (var set in new[] { g.CloneOf, g.Set })
        {
            if (string.IsNullOrEmpty(set)) continue;
            foreach (var (key, ports) in cfg.Mame.Games)
                if (key.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(set, StringComparer.OrdinalIgnoreCase))
                    foreach (var (k, v) in ports) d[k] = v;
        }
        return d;
    }

    /// <summary>
    /// A Ports binding as the launcher must read it. In Ports, JoyN is MAME's controller N (JOYCODE_N), which Devices
    /// can pin to a device; the launcher numbers joysticks in Windows' order, which differs (the X-Arcade's two halves
    /// come first there). So "Joy4.Button1" with JOYCODE_4 pinned to "Gamer-Pro Advanced.1" becomes
    /// "Joy[Gamer-Pro Advanced.1].Button1". Unpinned numbers are left as they are.
    /// </summary>
    public static IEnumerable<string> ForLauncher(IEnumerable<string> bindings, InputConfig cfg) =>
        bindings.Select(b => Regex.Replace(b, @"\bJoy(\d+)\.", m =>
            cfg.Mame.Devices.TryGetValue("JOYCODE_" + m.Groups[1].Value, out var dev) && !dev.Contains(']')
                ? $"Joy[{dev}]." : m.Value, RegexOptions.IgnoreCase));

    /// <summary>
    /// Character select: each slot a player chose gets that player's controls (joystick, buttons, Start, coin); a slot
    /// whose own player went elsewhere and nobody chose gets nothing. Returned as port changes for the game's section.
    /// </summary>
    static Dictionary<string, List<string>> Seat(Dictionary<string, List<string>> eff, Seating seats, int slots)
    {
        var off = seats.Off(slots).ToHashSet();
        // the kinds of control any player has ("_JOYSTICK_UP", "_BUTTON3", "START", "COIN"), as P<n><kind> / <kind><n>
        var kinds = new HashSet<string>();
        foreach (var type in eff.Keys)
        {
            var m = System.Text.RegularExpressions.Regex.Match(type.ToUpperInvariant(), @"^P[1-4](_.+)$|^(START|COIN)[1-4]$");
            if (m.Success) kinds.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        static string Type(string kind, int p) => kind.StartsWith('_') ? $"P{p}{kind}" : $"{kind}{p}";
        var r = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 1; slot <= Math.Max(4, slots); slot++)
        {
            bool chosen = seats.SideOfSlot.TryGetValue(slot, out int side);
            if (!chosen && !off.Contains(slot)) continue;     // its own player stayed out: leave it be
            foreach (var kind in kinds)
                r[Type(kind, slot)] = chosen ? eff.GetValueOrDefault(Type(kind, side)) ?? new() : new();
        }
        return r;
    }

    public static bool Write(InputConfig cfg, string mameDir, GameInfo? launching = null, Seating? seats = null)
    {
        if (!cfg.Mame.Generate) return false;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\"?>");
            sb.AppendLine("<!-- Written by the arcade launcher from config\\input.json at every launch: change that file, not this one. -->");
            sb.AppendLine("<mameconfig version=\"10\">");
            sb.AppendLine("  <system name=\"default\">");
            sb.AppendLine("    <input>");
            foreach (var (code, dev) in cfg.Mame.Devices)
                sb.AppendLine($"      <mapdevice device=\"{Esc(dev)}\" controller=\"{Esc(code)}\" />");
            Ports(sb, cfg.Mame.Ports, "Mame.Ports");
            sb.AppendLine("    </input>");
            sb.AppendLine("  </system>");
            // a Games key can name several sets ("sf2, sfa3"); a set named in more than one key gets all of them, later ones winning
            var games = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, ports) in cfg.Mame.Games)
                foreach (var set in key.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!games.TryGetValue(set, out var merged)) games[set] = merged = new();
                    foreach (var (type, list) in ports) merged[type] = list;
                }
            // games with few buttons: Mame.FewButtons, then the set's parent's and the set's own Games entries
            if (launching is { Buttons: > 0 } g && g.Buttons <= cfg.Mame.FewButtonsMax && cfg.Mame.FewButtons.Count > 0)
            {
                var merged = new Dictionary<string, List<string>>(cfg.Mame.FewButtons);
                foreach (var set in new[] { g.CloneOf, g.Set })
                    if (!string.IsNullOrEmpty(set) && games.TryGetValue(set, out var own))
                        foreach (var (type, list) in own) merged[type] = list;
                games[g.Set] = merged;
            }
            // character select: players' controls moved onto the slots they chose, for this run only
            if (launching != null && seats != null)
            {
                if (!games.TryGetValue(launching.Set, out var own)) games[launching.Set] = own = new();
                foreach (var (type, list) in Seat(EffectivePorts(cfg, launching), seats, launching.Characters.Count)) own[type] = list;
                Log.Write($"character select {launching.Set}: {seats}");
            }
            foreach (var (set, ports) in games)
            {
                sb.AppendLine($"  <system name=\"{Esc(set)}\">");
                sb.AppendLine("    <input>");
                Ports(sb, ports, "Mame.Games." + set);
                sb.AppendLine("    </input>");
                sb.AppendLine("  </system>");
            }
            sb.AppendLine("</mameconfig>");
            var file = Path.Combine(mameDir, "ctrlr", Name + ".cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file) || File.ReadAllText(file) != sb.ToString()) File.WriteAllText(file, sb.ToString());
            return true;
        }
        catch (Exception ex) { Log.Write("MAME controller file not written: " + ex.Message); return false; }
    }

    static void Ports(StringBuilder sb, Dictionary<string, List<string>> ports, string where)
    {
        foreach (var (type, list) in ports)
        {
            var seqs = new List<string>();
            foreach (var s in list)
                if (Seq(s, $"{where}.{type}") is string seq) seqs.Add(seq);
            string text = seqs.Count == 0 ? "NONE" : string.Join(" OR ", seqs.Distinct());
            sb.AppendLine($"      <port type=\"{Esc(type.ToUpperInvariant())}\"><newseq type=\"standard\">{Esc(text)}</newseq></port>");
        }
    }

    static string Esc(string s) => System.Security.SecurityElement.Escape(s);

    /// <summary>One binding as a MAME input sequence (a combination is MAME's "held together": codes side by side).</summary>
    static string? Seq(string binding, string where)
    {
        var parts = new List<string>();
        foreach (var raw in InputBinding.SplitCombo(binding))
        {
            var a = InputAtom.Parse(raw);
            string? tok = a == null ? null : Token(a);
            if (tok == null) { Log.Write($"input.json {where}: \"{raw.Trim()}\" has no MAME equivalent, left out (use Mame.<MAME code>)"); return null; }
            parts.Add(tok);
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    static string? Token(InputAtom a)
    {
        switch (a.Source)
        {
            case "mame": return a.Control.ToUpperInvariant();
            case "key": return Enum.TryParse<Keys>(a.Control, true, out var k) && KeyCode(k) is string kc ? "KEYCODE_" + kc : null;
            case "mouse":
                return a.Control.ToLowerInvariant() switch
                {
                    "left" => "MOUSECODE_1_BUTTON1", "right" => "MOUSECODE_1_BUTTON2", "middle" => "MOUSECODE_1_BUTTON3",
                    "x1" => "MOUSECODE_1_BUTTON4", "x2" => "MOUSECODE_1_BUTTON5", _ => null,
                };
            case "pad":
            {
                if (a.Index is not int n) return null; // MAME has no "any pad"
                string j = $"JOYCODE_{n}_";
                // MAME's XInput gamepad items (src/osd/modules/input/input_xinput.cpp)
                string? item = a.Control.ToLowerInvariant() switch
                {
                    "a" => "BUTTON1", "b" => "BUTTON2", "x" => "BUTTON3", "y" => "BUTTON4", "lb" => "BUTTON5", "rb" => "BUTTON6",
                    "ls" => "BUTTON7", "rs" => "BUTTON8", "start" => "START", "back" => "SELECT",
                    "up" => "HAT1UP", "down" => "HAT1DOWN", "left" => "HAT1LEFT", "right" => "HAT1RIGHT",
                    "lstickup" => "YAXIS_UP_SWITCH", "lstickdown" => "YAXIS_DOWN_SWITCH",
                    "lstickleft" => "XAXIS_LEFT_SWITCH", "lstickright" => "XAXIS_RIGHT_SWITCH",
                    // the triggers are MAME sliders that rest at 0 and go negative when pulled (checked on MAME 0.289)
                    "lt" => "SLIDER1_NEG_SWITCH", "rt" => "SLIDER2_NEG_SWITCH",
                    _ => null,  // Guide isn't visible to MAME; right stick: use Mame.<code> from MAME's input menu
                };
                return item == null ? null : j + item;
            }
            case "joy":
            {
                if (a.Index is not int n) return null;
                string j = $"JOYCODE_{n}_";
                int b = a.Control.StartsWith("Button", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Control[6..], out int bn) ? bn : 0;
                if (b > 0) return j + "BUTTON" + b;
                string? item = a.Control.ToLowerInvariant() switch
                {
                    "povup" => "HAT1UP", "povdown" => "HAT1DOWN", "povleft" => "HAT1LEFT", "povright" => "HAT1RIGHT",
                    "up" or "y-" => "YAXIS_UP_SWITCH", "down" or "y+" => "YAXIS_DOWN_SWITCH",
                    "left" or "x-" => "XAXIS_LEFT_SWITCH", "right" or "x+" => "XAXIS_RIGHT_SWITCH",
                    _ => null,
                };
                return item == null ? null : j + item;
            }
        }
        return null;
    }

    /// <summary>Windows key -> MAME KEYCODE_ name.</summary>
    static string? KeyCode(Keys k)
    {
        if (k >= Keys.A && k <= Keys.Z) return k.ToString();
        if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
        // Keypad names have no underscore (KEYCODE_8PAD, KEYCODE_PLUSPAD). MAME 0.289 drops a misspelt token from a
        // ctrlr file with "Dropping invalid input token", and parsing one sometimes crashes it at startup.
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return (int)(k - Keys.NumPad0) + "PAD";
        if (k >= Keys.F1 && k <= Keys.F24) return k.ToString();
        return k switch
        {
            Keys.Up => "UP", Keys.Down => "DOWN", Keys.Left => "LEFT", Keys.Right => "RIGHT",
            Keys.Enter => "ENTER", Keys.Escape => "ESC", Keys.Space => "SPACE", Keys.Tab => "TAB", Keys.Back => "BACKSPACE",
            Keys.ControlKey or Keys.LControlKey => "LCONTROL", Keys.RControlKey => "RCONTROL",
            Keys.ShiftKey or Keys.LShiftKey => "LSHIFT", Keys.RShiftKey => "RSHIFT",
            Keys.Menu or Keys.LMenu => "LALT", Keys.RMenu => "RALT", Keys.LWin => "LWIN", Keys.RWin => "RWIN", Keys.Apps => "MENU",
            Keys.Insert => "INSERT", Keys.Delete => "DEL", Keys.Home => "HOME", Keys.End => "END",
            Keys.PageUp => "PGUP", Keys.PageDown => "PGDN",
            Keys.OemMinus => "MINUS", Keys.Oemplus => "EQUALS", Keys.OemOpenBrackets => "OPENBRACE", Keys.OemCloseBrackets => "CLOSEBRACE",
            Keys.OemSemicolon => "COLON", Keys.OemQuotes => "QUOTE", Keys.Oemcomma => "COMMA", Keys.OemPeriod => "STOP",
            Keys.OemQuestion => "SLASH", Keys.OemPipe => "BACKSLASH", Keys.Oemtilde => "TILDE",
            Keys.Multiply => "ASTERISK", Keys.Add => "PLUSPAD", Keys.Subtract => "MINUSPAD", Keys.Divide => "SLASHPAD", Keys.Decimal => "DELPAD",
            Keys.CapsLock => "CAPSLOCK", Keys.NumLock => "NUMLOCK", Keys.Scroll => "SCRLOCK", Keys.Pause => "PAUSE", Keys.PrintScreen => "PRTSCR",
            _ => null,
        };
    }
}

/// <summary>--input-test: a console that names every control as it's pressed, ready to paste into input.json.</summary>
static class InputTest
{
    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    public static void Run()
    {
        AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.WriteLine("Arcade launcher input test. Press buttons, move sticks: each control's binding name is shown.");
        Console.WriteLine("Hold Escape for 2 seconds (or close this window) to quit.\n");
        InputDevices.Poll();
        foreach (int p in InputDevices.Pads()) Console.WriteLine($"XInput pad {p}  -> Pad{p}.<control>");
        foreach (var (n, id) in InputDevices.Joysticks()) Console.WriteLine($"Joystick {n}: {id}  -> Joy{n}.<control>  or  Joy[{id.Split(' ')[0]}].<control>");
        Console.WriteLine();
        var before = new HashSet<string>();
        double escHeld = 0;
        while (true)
        {
            InputDevices.Poll();
            var now = InputDevices.HeldNow().ToHashSet();
            foreach (var s in now.Except(before)) Console.WriteLine(s);
            before = now;
            escHeld = now.Contains("Key.Escape") ? escHeld + 0.016 : 0;
            if (escHeld > 2) return;
            Thread.Sleep(16);
        }
    }
}
