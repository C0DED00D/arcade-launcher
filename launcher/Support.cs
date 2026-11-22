using System.Diagnostics;
using System.Text.Json;

namespace ArcadeLauncher;

class Paths
{
    public readonly string Root;
    public Paths(string root) { Root = root; }
    public string Roms => Path.Combine(Root, "roms");
    public string MameDir => Path.Combine(Root, "emulators", "mame");
    public string MameExe => Path.Combine(MameDir, "mame.exe");
    public string Font => Path.Combine(MameDir, "uismall.bdf");
    public string Data => Path.Combine(Root, "data");
    public string LibraryCache => Path.Combine(Data, "library.json");
    public string Snaps => Path.Combine(Data, "snap");
    public string SnapWork => Path.Combine(Root, "work", "snapjob");
    public string RomAlias => Path.Combine(Root, "work", "romalias");
    public string JaguarDir => Path.Combine(Root, "jaguar");
    public string BigPEmuDir => Path.Combine(Root, "emulators", "bigpemu");
    public string BigPEmuExe => Path.Combine(BigPEmuDir, "BigPEmu.exe");
    public string BigPEmuConfig => Path.Combine(BigPEmuDir, "UserData", "BigPEmuConfig.bigpcfg");
    public string DreamcastDir => Path.Combine(Root, "dreamcast");
    public string FlycastDir => Path.Combine(Root, "emulators", "flycast");
    public string FlycastExe => Path.Combine(FlycastDir, "flycast.exe");
    public string FlycastConfig => Path.Combine(FlycastDir, "emu.cfg");  // portable: Flycast keeps it next to the exe
    public string SaturnDir => Path.Combine(Root, "saturn");
    public string MednafenDir => Path.Combine(Root, "emulators", "mednafen");
    public string MednafenExe => Path.Combine(MednafenDir, "mednafen.exe");  // portable: mednafen.cfg, firmware\, cheats\ beside it
    public string Settings => Path.Combine(Root, "config", "settings.json");
    public string InputConfig => Path.Combine(Root, "config", "input.json");
    public string GameOverrides => Path.Combine(Root, "config", "games.json");
    public string[] Catver => new[] { Path.Combine(Root, "config", "catver.ini"), Path.Combine(MameDir, "catver.ini") };
    public string SnapScript =>Path.Combine(Root, "config", "snaps.lua");
    public string LogFile => Path.Combine(Root, "app", "launcher.log");
}

static class Log
{
    static string? _file;
    static readonly object _lock = new();
    public static void Init(string file)
    {
        _file = file;
        try { if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Delete(file); } catch { }
    }
    public static void Write(string s)
    {
        if (_file == null) return;
        lock (_lock) { try { File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {s}\r\n"); } catch { } }
    }
}

public class Settings
{
    /// <summary>In-game CRT look: "hlsl" (MAME HLSL, default), "geom" (bgfx crt-geom deluxe), "off".</summary>
    public string GameCrt { get; set; } = "hlsl";
    /// <summary>Free play in every game (MAME plugin "freeplay": Free Play DIP switch, or Start inserts a coin).</summary>
    public bool FreePlay { get; set; } = true;
    public bool MenuCrt { get; set; } = true;
    public bool Sounds { get; set; } = true;
    public string LastGame { get; set; } = "";
    /// <summary>Seconds holding the ExitGame hotkey (config\input.json; default Guide, or Back + Start) to leave a game.</summary>
    public double ExitHoldSeconds { get; set; } = 1.2;
    /// <summary>Extra MAME command-line options for every game.</summary>
    public string MameExtraArgs { get; set; } = "";
    /// <summary>Leave light gun games out of the menu (for a cabinet without a gun).</summary>
    public bool HideLightGunGames { get; set; } = true;
    /// <summary>Older versions' menu keys. Read once to seed config\input.json, which holds every control now.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Keys { get; set; }
    public CrtSettings Crt { get; set; } = new();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Opts) ?? new();
            }
        }
        catch (Exception ex) { Log.Write("settings.json unreadable, using defaults: " + ex.Message); }
        return new();
    }
    public void Save(string path)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}

/// <summary>Plays each game's attract mode headless (no window, no sound, as fast as possible) and keeps its best frames.</summary>
class SnapshotJob
{
    readonly Paths _paths;
    readonly Library _lib;
    Process? _proc;
    volatile bool _stop, _running;
    public bool Running => _running;
    public event Action<string>? SnapsReady;
    public string? Current { get; private set; }

    public SnapshotJob(Paths paths, Library lib) { _paths = paths; _lib = lib; }

    public static string[] SnapsFor(Paths p, string set)
    {
        var dir = Path.Combine(p.Snaps, set);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToArray() : Array.Empty<string>();
    }

    public void Start(IEnumerable<GameInfo> games)
    {
        if (_running) return;
        // Attract-mode grabs use MAME; console games get their previews by hand (data\snap\<set>\*.png).
        var todo = games.Where(g => g.System == "arcade" && g.Runnable && !g.Hidden && SnapsFor(_paths, g.Set).Length == 0).ToList();
        if (todo.Count == 0) return;
        _stop = false;
        _running = true;
        new Thread(() =>
        {
            foreach (var g in todo)
            {
                if (_stop) break;
                try { Grab(g); } catch (Exception ex) { Log.Write($"snapshots {g.Set}: {ex.Message}"); }
            }
            Current = null;
            _running = false;
        }) { IsBackground = true, Name = "snapshots", Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Grab(GameInfo g)
    {
        Current = g.Set;
        var work = _paths.SnapWork;
        var outDir = Path.Combine(work, "out");
        if (Directory.Exists(Path.Combine(outDir, g.Set))) Directory.Delete(Path.Combine(outDir, g.Set), true);
        Directory.CreateDirectory(outDir);
        // Separate cfg/nvram so the attract run never touches the player's settings or high scores. It starts from a copy of
        // the real nvram, though: some games only boot once set up (e.g. ones that install themselves into flash memory).
        try
        {
            var realNv = Path.Combine(_paths.MameDir, "nvram", g.Set);
            var workNv = Path.Combine(work, "nvram", g.Set);
            if (Directory.Exists(realNv))
            {
                if (Directory.Exists(workNv)) Directory.Delete(workNv, true);
                Directory.CreateDirectory(workNv);
                foreach (var f in Directory.GetFiles(realNv)) File.Copy(f, Path.Combine(workNv, Path.GetFileName(f)));
            }
        }
        catch (Exception e) { Log.Write($"snapshots {g.Set}: nvram copy failed: {e.Message}"); }
        string args = $"{g.Set} -video none -sound none -nothrottle -seconds_to_run 150 -skip_gameinfo " +
                      $"-autoboot_script \"{_paths.SnapScript}\" -snapshot_directory \"{outDir}\" -snapname %g/%i " +
                      $"-cfg_directory \"{Path.Combine(work, "cfg")}\" -nvram_directory \"{Path.Combine(work, "nvram")}\" " +
                      $"-diff_directory \"{Path.Combine(work, "diff")}\"";
        // Vector games: render the snapshot bigger with fat beams, otherwise it's faint 1-pixel lines.
        if (g.Vector) args += " -snapsize 640x480 -beam_width_min 2.5 -beam_width_max 4 -beam_intensity_weight 0.5";
        var psi = new System.Diagnostics.ProcessStartInfo(_paths.MameExe, args)
        { WorkingDirectory = _paths.MameDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        var sw = Stopwatch.StartNew();
        _proc = Process.Start(psi)!;
        try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _proc.OutputDataReceived += (_, _) => { }; _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();
        if (!_proc.WaitForExit(180000)) { try { _proc.Kill(); } catch { } }
        _proc = null;
        if (_stop) return;
        var shots = Directory.Exists(Path.Combine(outDir, g.Set)) ? Directory.GetFiles(Path.Combine(outDir, g.Set), "*.png") : Array.Empty<string>();
        // Keep the 4 most interesting frames (skips black screens and plain warning text), in time order.
        var best = shots.Select(f => (f, score: Img.Load(f).Interest())).OrderByDescending(x => x.score).Take(4).Select(x => x.f).OrderBy(f => f).ToList();
        var dst = Path.Combine(_paths.Snaps, g.Set);
        Directory.CreateDirectory(dst);
        for (int i = 0; i < best.Count; i++) File.Copy(best[i], Path.Combine(dst, $"{i + 1}.png"), true);
        Log.Write($"snapshots {g.Set}: {best.Count} kept of {shots.Length} in {sw.Elapsed.TotalSeconds:0.0}s");
        SnapsReady?.Invoke(g.Set);
    }

    /// <summary>Stops the background MAME (before a real game starts).</summary>
    public void Stop()
    {
        _stop = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
    }
}
