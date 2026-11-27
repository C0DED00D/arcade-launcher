using System.Diagnostics;
using System.Text;

namespace ArcadeLauncher;

/// <summary>Runs one game in MAME and ends it when the exit combo is held.</summary>
sealed class GameRunner
{
    readonly Paths _paths;
    readonly Process _proc;
    readonly StringBuilder _err = new();
    readonly DateTime _started = DateTime.Now;
    double _held;
    bool _closing;
    DateTime _closeAt;
    readonly InputConfig _input;
    public GameInfo Game { get; }

    public GameRunner(Paths paths, GameInfo g, Settings s, InputConfig input)
    {
        _paths = paths; Game = g; _input = input;
        _saveKey = new(0x75, 0x40, input.SaveState);   // F6
        _loadKey = new(0x76, 0x41, input.LoadState);   // F7
        _pauseKey = new(0x50, 0x19, input.Pause);      // P = MAME's UI_PAUSE
        _pauseSwitch = new(0x50, 0x19, input.PauseSwitch);
        var psi = g.System switch
        {
            "jaguar" => JaguarStart(paths, g, s),
            "dreamcast" => FlycastStart(paths, g, s),
            "saturn" => MednafenStart(paths, g),
            _ => MameStart(paths, g, s, input),
        };
        Log.Write($"start: {Path.GetFileNameWithoutExtension(psi.FileName)} {psi.Arguments}");
        _proc = Process.Start(psi)!;
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_err) _err.AppendLine(e.Data); };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_err) _err.AppendLine(e.Data); };
        _proc.BeginErrorReadLine(); _proc.BeginOutputReadLine();
        Win32.AllowSetForegroundWindow(_proc.Id);
    }

    // Sega Saturn: Mednafen (portable: mednafen.cfg, firmware\mpr-17933.bin = US BIOS, cheats\ss.cht beside the exe).
    // Settings given on the command line apply to this run only, so Mednafen's own config isn't fought over.
    // Cheats (e.g. credits locked at 99) live in cheats\ss.cht, keyed by the disc's MD5.
    static ProcessStartInfo MednafenStart(Paths paths, GameInfo g)
    {
        var args = "-video.fs 1 -ss.region_autodetect 1 -ss.region_default na -cheats 1 -ss.shader goat -ss.stretch aspect";
        return new ProcessStartInfo(paths.MednafenExe, $"{args} \"{g.FilePath}\"")
        {
            WorkingDirectory = paths.MednafenDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
    }

    // Atari Jaguar: BigPEmu, borderless full screen, with the arcade CRT effect (Data\ScreenEffects\arcade_crt.dstack,
    // a port of the menu's CRT shader). Its settings live in emulators\bigpemu\UserData (-localdata).
    static ProcessStartInfo JaguarStart(Paths paths, GameInfo g, Settings s)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.BigPEmuConfig))!;
            var cfg = root["BigPEmuConfig"]!;
            cfg["VideoPlugin"] = "BigPEmu_Video_OpenGL";        // the CRT effect is GLSL
            cfg["ScreenEffect"] = s.GameCrt == "off" ? "" : "arcade_crt.dstack";
            cfg["Video"]!["DisplayMode"] = 0;                   // borderless window covering the display
            cfg["Input"]!["AutoAssign"] = 1;                    // map any recognised pad automatically
            File.WriteAllText(paths.BigPEmuConfig, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Write("BigPEmu config not updated: " + e.Message); }
        return new ProcessStartInfo(paths.BigPEmuExe, $"\"{g.FilePath}\" -localdata")
        {
            WorkingDirectory = paths.BigPEmuDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
    }

    // Dreamcast and Naomi: Flycast, full screen, US console, free play on Naomi boards. Flycast keeps its settings in
    // emulators\flycast\emu.cfg (portable). Only the keys below are forced; anything set in Flycast's own menu stays.
    static ProcessStartInfo FlycastStart(Paths paths, GameInfo g, Settings s)
    {
        try
        {
            var config = new Dictionary<string, string>
            {
                    ["Dreamcast.ContentPath"] = paths.DreamcastDir,
                    ["Dreamcast.BiosPath"] = paths.DreamcastDir,       // dc_boot.bin / naomi.zip, if supplied
                    ["Dreamcast.Region"] = "1",                        // USA
                    ["Dreamcast.Broadcast"] = "0",                     // NTSC
                    ["Dreamcast.Language"] = "1",                      // English
                    ["ForceFreePlay"] = s.FreePlay ? "yes" : "no",     // Naomi / Atomiswave free play
                    ["rend.Resolution"] = "1440",                      // 3x the Dreamcast's 480 lines (3D games)
                    ["rend.TextureFiltering"] = "0",                   // game's own choice
                    ["UploadCrashLogs"] = "no",
                    ["DiscordPresence"] = "no",
                    ["FetchBoxart"] = "no",
            };
            // Per-game keys from config\games.json "flycast" (e.g. sprite games at the native 480 lines). Defaults
            // above are written every launch, so one game's setting never leaks into the next.
            foreach (var (k, v) in g.Flycast) config[k] = v;
            IniSet(paths.FlycastConfig, new() { ["window"] = new() { ["fullscreen"] = "yes" }, ["config"] = config });
        }
        catch (Exception e) { Log.Write("Flycast config not updated: " + e.Message); }
        // CRT filter: ReShade (emulators\flycast\dxgi.dll, shader reshade-shaders\Shaders\ArcadeCRT.fx). Only for games
        // marked "crt": true in games.json, and not when the menu's game CRT setting (Y) is off. Switched by renaming
        // the DLL, so a game without it doesn't load ReShade at all.
        try
        {
            bool crt = g.Crt && s.GameCrt != "off";
            string on = Path.Combine(paths.FlycastDir, "dxgi.dll"), off = on + ".off";
            if (crt && File.Exists(off)) { File.Move(off, on, true); }
            else if (!crt && File.Exists(on)) { File.Move(on, off, true); }
            Log.Write($"Flycast CRT filter: {(crt ? "on" : "off")}");
        }
        catch (Exception e) { Log.Write("CRT filter switch failed: " + e.Message); }
        return new ProcessStartInfo(paths.FlycastExe, $"\"{g.FilePath}\"")
        {
            WorkingDirectory = paths.FlycastDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
    }

    /// <summary>Sets keys in a plain INI file, keeping every other line as it was.</summary>
    static void IniSet(string file, Dictionary<string, Dictionary<string, string>> want)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        foreach (var (section, keys) in want)
        {
            int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
                lines.Add($"[{section}]"); start = lines.Count - 1;
            }
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            foreach (var (key, value) in keys)
            {
                int at = -1;
                for (int i = start + 1; i < end; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                }
                string line = $"{key} = {value}";
                if (at >= 0) lines[at] = line;
                else
                {
                    int ins = end;
                    while (ins > start + 1 && lines[ins - 1].Trim() == "") ins--; // before the blank line ending the section
                    lines.Insert(ins, line); end++;
                }
            }
        }
        File.WriteAllLines(file, lines);
    }

    static ProcessStartInfo MameStart(Paths paths, GameInfo g, Settings s, InputConfig input)
    {
        var args = new StringBuilder(g.Set);
        // Controls: ctrlr\launcher.cfg, written from config\input.json "Mame" (unless Generate is off: then mame.ini's ctrlr)
        if (MameCtrlr.Write(input, paths.MameDir, g)) args.Append(" -ctrlr ").Append(MameCtrlr.Name);
        // In-game CRT look. Raster games: CRT-Guest-Advanced through ReShade (emulators\mame\d3d9.dll, preset
        // reshade\GuestCRT.ini). Vector games keep MAME's HLSL (ini\vector.ini): scanlines don't belong on a vector screen.
        bool guest = false;
        switch (s.GameCrt)
        {
            case "geom":
                if (!g.Vector) args.Append(" -video bgfx -bgfx_screen_chains arcade-geom");
                break;
            case "off":
                args.Append(" -video d3d -nohlsl_enable");
                break;
            default:
                if (!g.Vector && g.Width > 0 && g.Height > 0 && (File.Exists(Path.Combine(paths.MameDir, "d3d9.dll.off")) || File.Exists(Path.Combine(paths.MameDir, "d3d9.dll"))))
                {
                    guest = true;
                    args.Append(" -video d3d -nohlsl_enable -nofilter");
                }
                break;
        }
        SetMameReShade(paths, g, guest);
        // Free play: plugins\freeplay sets the game's Free Play DIP switch, or makes Start insert a coin first.
        if (!s.FreePlay) args.Append(" -noplugin freeplay");
        StripTakenOverStart(paths, g.Set);
        if (!string.IsNullOrWhiteSpace(s.MameExtraArgs)) args.Append(' ').Append(s.MameExtraArgs);
        var psi = new ProcessStartInfo(paths.MameExe, args.ToString())
        {
            WorkingDirectory = paths.MameDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        // plugins\savestates and plugins\charselect create this file while their screens are up (see ReShadeForPicker).
        // Only this MAME gets the path, so another MAME (a test run) can't switch this game's CRT effect.
        try { File.Delete(MenuMarker(paths)); } catch { }
        psi.Environment["ARCADE_MENU_MARKER"] = MenuMarker(paths);
        return psi;
    }

    static string MenuMarker(Paths paths) => Path.Combine(Path.GetTempPath(), "arcade-launcher-menu.open");

    /// <summary>
    /// Turns ReShade on or off for MAME (renaming d3d9.dll) and tells CRT-Guest the game's resolution. Guest resamples
    /// the whole screen at Resolution_X x Resolution_Y, and MAME draws the game as a 4:3 picture in the middle, so X
    /// is the game's width stretched by how much wider the screen is than the picture.
    /// </summary>
    static void SetMameReShade(Paths paths, GameInfo g, bool on)
    {
        try
        {
            string dll = Path.Combine(paths.MameDir, "d3d9.dll"), off = dll + ".off";
            if (on && File.Exists(off)) File.Move(off, dll, true);
            else if (!on && File.Exists(dll)) File.Move(dll, off, true);
            if (!on) return;
            // Vertical games: GUEST_ROTATE has the shader turn the picture back into the game's own landscape raster
            // (exactly the picture, so no widening), so scanlines run along the game's real lines.
            int rotate = g.Rotate == 90 ? 1 : g.Rotate == 270 ? 2 : 0;
            int resX = g.Width, resY = g.Height;
            if (rotate == 0)
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                resX = (int)Math.Round(g.Width * Math.Max(1.0, (double)screen.Width / screen.Height / (4.0 / 3.0)));
            }
            var preset = Path.Combine(paths.MameDir, "reshade", "GuestCRT.ini");
            var lines = File.Exists(preset) ? File.ReadAllLines(preset).ToList() : new List<string> { "Techniques=CRT_Guest_Advanced@CRT-Guest-Advanced.fx" };
            string def = $"PreprocessorDefinitions=GUEST_ROTATE={rotate},Resolution_X={resX},Resolution_Y={resY}";
            int at = lines.FindIndex(l => l.StartsWith("PreprocessorDefinitions=", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = def; else lines.Insert(Math.Min(2, lines.Count), def);
            File.WriteAllLines(preset, lines);
            Log.Write($"CRT-Guest: {g.Width}x{g.Height} rotate {g.Rotate} -> GUEST_ROTATE {rotate}, Resolution {resX}x{resY}");
        }
        catch (Exception e) { Log.Write("ReShade setup for MAME failed: " + e.Message); }
    }

    // In coin-on-start games the freeplay plugin empties the Start mapping while the game runs, and MAME saves that
    // into cfg\<set>.cfg on exit. Remove those entries so the normal mapping (ctrlr\arcade.cfg) applies again.
    static void StripTakenOverStart(Paths paths, string set)
    {
        try
        {
            var file = Path.Combine(paths.MameDir, "cfg", set + ".cfg");
            if (!File.Exists(file)) return;
            var text = File.ReadAllText(file);
            var fixedText = System.Text.RegularExpressions.Regex.Replace(text,
                @"\s*<port tag=""[^""]*"" type=""START\d""[^>]*>\s*<newseq type=""standard"">\s*(NONE)?\s*</newseq>\s*</port>", "");
            if (fixedText != text) File.WriteAllText(file, fixedText);
        }
        catch (Exception e) { Log.Write("cfg cleanup failed: " + e.Message); }
    }

    public bool Exited => _proc.HasExited;
    public int ExitCode => _proc.ExitCode;
    public TimeSpan RunTime => DateTime.Now - _started;
    public string ErrorText { get { lock (_err) return _err.ToString(); } }
    /// <summary>0..1 while the exit combo is being held (for the on-screen bar; MAME is on top so mostly for the log).</summary>
    public double HoldProgress { get; private set; }

    /// <summary>Called ~30 times a second while the game runs.</summary>
    public void Tick(double dt, double holdSeconds)
    {
        if (_proc.HasExited) { ReleaseStateKeys(); return; }
        if (_closing)
        {
            if (DateTime.Now > _closeAt) { Log.Write("emulator didn't close, killing it"); try { _proc.Kill(); } catch { } _closeAt = DateTime.MaxValue; }
            return;
        }
        if (Game.System == "arcade") { DismissWarning(); StateKeys(); ReShadeForPicker(); }
        InputDevices.Poll();
        if (_saveExitAt is DateTime at)
        {
            if (DateTime.Now >= at) { _saveExitAt = null; Close("exit combo held (saved)"); }
            return;
        }
        _held = _input.ExitGame.IsDown(GameInput.Key) ? _held + dt : 0;
        HoldProgress = Math.Min(1, _held / holdSeconds);
        if (_held >= holdSeconds)
        {
            if (SavesOnExit()) SaveThenClose();
            else Close("exit combo held");
        }
    }

    // input.json Mame.SaveOnExit: leaving with the exit combo first presses the save key (F6, plugins\savestates; it
    // saves even while MAME is paused), then closes MAME once the save has had time to be written.
    DateTime? _saveExitAt;
    bool SavesOnExit() => Game.System == "arcade" && _input.Mame.SaveOnExit.Any(s => s == "*" ||
        s.Equals(Game.Set, StringComparison.OrdinalIgnoreCase) || s.Equals(Game.CloneOf, StringComparison.OrdinalIgnoreCase));
    void SaveThenClose()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint fgPid);
        if (fgPid != (uint)_proc.Id) { Close("exit combo held (not saved: MAME didn't have the focus)"); return; }
        if (_saveKey.Down) _saveKey.Release();
        _saveKey.Press();
        Thread.Sleep(120); // MAME reads keys once a frame: hold long enough to be seen (StateKeys doesn't run while waiting)
        _saveKey.Release();
        _saveExitAt = DateTime.Now.AddSeconds(1.5);
        Log.Write("exit combo held: saving a state before closing");
    }

    // MAME stops on a "known problems with this system - press any key" screen the first time a game with
    // warnings runs. Tap Enter (unused in game) every 2 s for the first 15 s after the window appears (slow boots
    // show it late); MAME then records the warning as seen (ui.ini skip_warnings) and won't show it again.
    // Enter is held for a few ticks: MAME reads keys once per frame and misses an instant down+up.
    DateTime? _windowSeen;
    DateTime _nextTap;
    DateTime? _enterDownAt;
    void DismissWarning()
    {
        if (_enterDownAt is DateTime down)
        {
            if ((DateTime.Now - down).TotalMilliseconds < 100) return;
            Win32.keybd_event(0x0D, 0x1C, 2, UIntPtr.Zero);  // Enter up
            _enterDownAt = null;
            return;
        }
        var wins = Win32.WindowsOf(_proc.Id);
        if (wins.Count == 0) return;
        if (_windowSeen == null) { _windowSeen = DateTime.Now; _nextTap = DateTime.Now.AddSeconds(1.5); }
        if ((DateTime.Now - _windowSeen.Value).TotalSeconds > 15 || DateTime.Now < _nextTap) return;
        // Full screen MAME ignores posted key messages, so press Enter for real - but only while MAME has the focus,
        // so the key can never land in another program.
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint fgPid);
        if (fgPid != (uint)_proc.Id)
        {
            Win32.SetForegroundWindow(wins[0]);
            return; // try again next tick
        }
        Win32.keybd_event(0x0D, 0x1C, 0, UIntPtr.Zero);  // Enter down
        _enterDownAt = DateTime.Now;
        _nextTap = DateTime.Now.AddSeconds(2);
    }

    // Save states (MAME plugins\savestates listens for F6 = save, F7 = load; a held F7 opens its picker).
    // input.json Hotkeys.SaveState / LoadState (default: right / left stick click; held = picker). The key mirrors the binding, held for at
    // least 100 ms so MAME (which reads keys once a frame) never misses a quick click, and only while MAME has the focus.
    // Hotkeys.Pause (none by default) is pressed as P the same way, so a mouse button or pad combo can pause MAME
    // even in games that run with mouse input off.
    readonly StateKey _saveKey, _loadKey, _pauseKey;
    sealed class StateKey(byte vk, byte scan, InputBindings binding)
    {
        public bool Down; public DateTime At;
        public bool PadHeld() => binding.IsDown(null); // keyboard bindings skipped: a key would press F6/F7 for itself
        public void Press() { Win32.keybd_event(vk, scan, 0, UIntPtr.Zero); Down = true; At = DateTime.Now; }
        public void Release() { Win32.keybd_event(vk, scan, 2, UIntPtr.Zero); Down = false; }
    }
    void StateKeys()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint fgPid);
        bool focused = fgPid == (uint)_proc.Id;
        foreach (var k in new[] { _saveKey, _loadKey, _pauseKey })
        {
            bool held = focused && k.PadHeld();
            if (held && !k.Down) k.Press();
            else if (!held && k.Down && (DateTime.Now - k.At).TotalMilliseconds >= 100) k.Release();
        }
        PauseSwitch(focused);
    }

    // Hotkeys.PauseSwitch: an on/off switch (the Star Wars yoke's power switch). Turning it on pauses MAME, turning it
    // off resumes, by tapping P. Only changes count, so the position it's in at launch does nothing, and a switch
    // turned off while the game runs leaves it running.
    readonly StateKey _pauseSwitch;
    bool? _switchOn;
    bool _switchPaused;
    void PauseSwitch(bool focused)
    {
        bool on = _pauseSwitch.PadHeld();
        if (_pauseSwitch.Down && (DateTime.Now - _pauseSwitch.At).TotalMilliseconds >= 100) _pauseSwitch.Release();
        if (_switchOn == null) { _switchOn = on; return; }
        if (on == _switchOn) return;
        _switchOn = on;
        bool wantPaused = on;
        if (wantPaused == _switchPaused || !focused || _pauseSwitch.Down) return;
        _pauseSwitch.Press();
        _switchPaused = wantPaused;
        Log.Write(wantPaused ? "pause switch on: pausing" : "pause switch off: resuming");
    }
    // The save-state picker is unreadable through the CRT shader. plugins\savestates (and charselect) create the marker
    // file (MenuMarker, passed to MAME in ARCADE_MENU_MARKER) while their screens show;
    // tap ReShade's toggle-effects key (F13, ReShade.ini KeyEffects=124) when that appears and again when it goes.
    // ReShade only hooks input once MAME's window is up, so the first tap waits a moment after the window appears.
    bool _effectsOff;
    DateTime? _f13UpAt;
    void ReShadeForPicker()
    {
        if (_f13UpAt is DateTime up) { if (DateTime.Now >= up) { Win32.keybd_event(0x7C, 0x64, 2, UIntPtr.Zero); _f13UpAt = null; } return; }
        if (!File.Exists(Path.Combine(_paths.MameDir, "d3d9.dll"))) return; // ReShade isn't on for this game
        bool open = File.Exists(MenuMarker(_paths));
        if (open == _effectsOff) return;
        if (_windowSeen == null || (DateTime.Now - _windowSeen.Value).TotalSeconds < 1.5) return;
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint fgPid);
        if (fgPid != (uint)_proc.Id) return;
        Win32.keybd_event(0x7C, 0x64, 0, UIntPtr.Zero);
        _f13UpAt = DateTime.Now.AddMilliseconds(100);
        _effectsOff = open;
        Log.Write(open ? "save picker open: CRT effect off" : "save picker closed: CRT effect back on");
    }

    void ReleaseStateKeys() { foreach (var k in new[] { _saveKey, _loadKey, _pauseKey, _pauseSwitch }) if (k.Down) k.Release(); }

    public void Close(string why)
    {
        if (_closing || _proc.HasExited) return;
        ReleaseStateKeys();
        _closing = true;
        _closeAt = DateTime.Now.AddSeconds(5);
        Log.Write("closing the emulator: " + why);
        // WM_CLOSE lets the emulator save NVRAM (high scores), memory cards and settings properly.
        foreach (var h in Win32.WindowsOf(_proc.Id)) Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Kill() { try { if (!_proc.HasExited) _proc.Kill(); } catch { } }
}
