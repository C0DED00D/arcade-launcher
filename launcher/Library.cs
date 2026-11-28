using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ArcadeLauncher;

public class GameInfo
{
    public string System { get; set; } = "arcade";  // "arcade" (MAME), "jaguar" (BigPEmu) or "dreamcast" (Flycast: discs and Naomi zips)
    public string? FilePath { get; set; }           // console games: the image to load
    public string Zip { get; set; } = "";           // file name in roms\
    public long ZipSize { get; set; }
    public DateTime ZipTime { get; set; }
    public string Set { get; set; } = "";           // MAME short name
    public string? Alias { get; set; }              // set when the zip isn't named after its MAME set
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";       // the bracketed part of MAME's description, e.g. "US"
    public string Year { get; set; } = "";
    public string Maker { get; set; } = "";
    public int Players { get; set; }
    public int Buttons { get; set; }
    public List<string> Controls { get; set; } = new();
    public bool Vector { get; set; }
    public int Rotate { get; set; }
    public int Width { get; set; }                  // native raster size (before rotation), for the CRT shader
    public int Height { get; set; }
    public int Screens { get; set; } = 1;           // monitors (X-Men 6P: 2, shown side by side with no gap)
    public string Emulation { get; set; } = "";     // MAME driver status: good / imperfect / preliminary
    public bool Runnable { get; set; }
    public List<string> Missing { get; set; } = new();
    public bool Hidden { get; set; }                // BIOS / device zips, or unknown files
    public string? Problem { get; set; }
    /// <summary>Menu genre (config\games.json "genre", else catver.ini, else OTHER). Worked out at every start.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Genre { get; set; } = "OTHER";
    /// <summary>The set this one is a clone of (catver.ini fallback).</summary>
    public string CloneOf { get; set; } = "";
    /// <summary>Flycast [config] keys for this game only (config\games.json "flycast"), e.g. native resolution for sprites.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, string> Flycast { get; set; } = new();
    /// <summary>Dreamcast games: run with the ReShade CRT filter (config\games.json "crt": true). Off unless asked for.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Crt { get; set; }
    /// <summary>
    /// Games whose player slots are fixed characters (config\games.json "characters", one per slot in slot order; a
    /// clone uses its parent's): the launcher shows a character select first (CharSelect).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<Character> Characters { get; set; } = new();
    public sealed record Character(string Name, uint Color);

    public string ControlText()
    {
        var parts = new List<string>();
        foreach (var c in Controls)
            parts.Add(c switch
            {
                "joy8" => "8-way joystick", "joy4" => "4-way joystick", "joy2" => "2-way joystick", "doublejoy" => "twin sticks",
                "stick" => "analog yoke", "dial" => "spinner", "paddle" => "paddle", "trackball" => "trackball",
                "pedal" => "pedal", "lightgun" => "light gun", "positional" => "wheel", "jaguar" => "Atari Jaguar pad", "dcpad" => "Dreamcast pad", "sspad" => "Saturn pad",
                _ => c,
            });
        if (Buttons > 0) parts.Add(Buttons == 1 ? "1 button" : $"{Buttons} buttons");
        return string.Join(" + ", parts.Distinct());
    }

    /// <summary>How the analog controls land on an XInput pad.</summary>
    public string? PadHint()
    {
        if (Controls.Contains("dial") || Controls.Contains("paddle")) return "Spin with the left stick / d-pad";
        if (Controls.Contains("stick")) return "Yoke on the left stick";
        if (Controls.Contains("trackball")) return "Use the trackball / mouse";
        if (Controls.Contains("positional")) return "Wheel on the left stick";
        return null;
    }
}

/// <summary>Finds the ROMs, asks MAME what they are, checks them, and caches the answers in data\library.json.</summary>
class Library
{
    readonly Paths _paths;
    public List<GameInfo> Games { get; private set; } = new();
    public string MameVersion { get; private set; } = "";

    public Library(Paths paths) { _paths = paths; }

    record Cache(string MameVersion, List<GameInfo> Games);

    public void Scan(Action<string, string>? progress)
    {
        MameVersion = RunMame("-version").Trim();
        progress?.Invoke("MAME", MameVersion.Split(' ')[0]);
        Cache? cache = null;
        try { if (File.Exists(_paths.LibraryCache)) cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(_paths.LibraryCache)); } catch { }
        if (cache?.MameVersion != MameVersion) cache = null;

        var result = new List<GameInfo>();
        // Sets come as .zip, .7z (often merged: the parent plus all its clones) or a plain folder of ROM files.
        var files = Directory.Exists(_paths.Roms)
            ? Directory.GetFiles(_paths.Roms, "*.zip").Concat(Directory.GetFiles(_paths.Roms, "*.7z")).Concat(Directory.GetDirectories(_paths.Roms)).OrderBy(f => f)
            : Enumerable.Empty<string>();
        foreach (var file in files)
        {
            bool isDir = Directory.Exists(file);
            var fi = new FileInfo(file);
            long size = isDir ? Directory.GetFiles(file).Sum(f => new FileInfo(f).Length) : fi.Length;
            DateTime time = isDir ? Directory.GetFiles(file).Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty(fi.LastWriteTimeUtc).Max() : fi.LastWriteTimeUtc;
            var cached = cache?.Games.FirstOrDefault(g => g.Zip.Equals(fi.Name, StringComparison.OrdinalIgnoreCase) && g.ZipSize == size && g.ZipTime == time);
            // Bad sets are re-checked every start, so dropping the missing files in turns the game on.
            if (cached != null && (cached.Runnable || cached.Hidden))
            {
                if (cached.Alias != null) EnsureAlias(file, cached.Alias);
                // entries cached before the CRT shader needed the screen size: fill it in once
                if (!cached.Hidden && !cached.Vector && cached.Width == 0) { bool run = cached.Runnable; var prob = cached.Problem; ReadXml(cached, cached.Set); cached.Runnable = run; cached.Problem = prob; }
                result.Add(cached);
                progress?.Invoke(cached.Set.ToUpperInvariant(), cached.Hidden ? "SKIP" : "OK");
                continue;
            }
            GameInfo g;
            if (cached != null && cached.Set != "")
            {
                g = cached; // known set that was incomplete: only check the files again
                if (g.Alias != null) EnsureAlias(file, g.Alias);
            }
            else g = Identify(fi, size, time, isDir);
            if (!g.Hidden) Verify(g);
            result.Add(g);
            progress?.Invoke((g.Set != "" ? g.Set : Path.GetFileNameWithoutExtension(fi.Name)).ToUpperInvariant(),
                g.Hidden ? "SKIP" : g.Runnable ? "OK" : "BAD");
        }
        // The same set from two files (e.g. <set>.zip and <set>.7z): MAME combines them, so list the game once.
        result = result.GroupBy(g => g.Hidden ? "\0" + g.Zip : g.Set)
            .Select(grp => grp.OrderByDescending(g => g.Runnable).First()).OrderBy(g => g.Zip).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.LibraryCache)!);
        File.WriteAllText(_paths.LibraryCache, JsonSerializer.Serialize(new Cache(MameVersion, result), new JsonSerializerOptions { WriteIndented = true }));
        result.AddRange(ScanJaguar(progress));
        result.AddRange(ScanDreamcast(progress));
        result.AddRange(ScanSaturn(progress));
        ApplyOverrides(result);
        Games = result;
    }

    static readonly string[] JaguarExts = { ".j64", ".jag", ".rom", ".abs", ".cof", ".bin", ".cue", ".cdi", ".bigpimg" };

    /// <summary>Atari Jaguar images in jaguar\, run by BigPEmu. No checking needed: BigPEmu boots them without a BIOS.</summary>
    List<GameInfo> ScanJaguar(Action<string, string>? progress)
    {
        var list = new List<GameInfo>();
        if (!Directory.Exists(_paths.JaguarDir) || !File.Exists(_paths.BigPEmuExe)) return list;
        foreach (var file in Directory.GetFiles(_paths.JaguarDir).Where(f => JaguarExts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            string name = Path.GetFileNameWithoutExtension(fi.Name);
            // "Some Game (World)" -> title "Some Game", version "World"
            var tags = Regex.Matches(name, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
            string title = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
            string set = "jag_" + Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "");
            var g = new GameInfo
            {
                System = "jaguar", FilePath = file, Zip = fi.Name, ZipSize = fi.Length, ZipTime = fi.LastWriteTimeUtc,
                Set = set, Title = title, Version = string.Join(", ", tags.Prepend("Atari Jaguar")), Maker = "",
                Controls = new() { "jaguar" }, Runnable = true,
            };
            list.Add(g);
            progress?.Invoke(set.ToUpperInvariant(), "JAG");
        }
        return list;
    }

    // Disc images Flycast boots. A .gdi or .cue comes with its track files (usually in a folder of its own), so only
    // the .gdi/.cue is listed, never the tracks.
    static readonly string[] DreamcastExts = { ".chd", ".gdi", ".cdi", ".cue" };

    /// <summary>
    /// Dreamcast discs and Naomi/Atomiswave arcade zips in dreamcast\ (and its subfolders), run by Flycast.
    /// Discs need no BIOS (Flycast has a built-in one); Naomi zips and flat Naomi images (.dat/.lst/.bin) need naomi.zip
    /// (awbios.zip for Atomiswave) beside them.
    /// </summary>
    List<GameInfo> ScanDreamcast(Action<string, string>? progress)
    {
        var list = new List<GameInfo>();
        if (!Directory.Exists(_paths.DreamcastDir) || !File.Exists(_paths.FlycastExe)) return list;
        var biosZips = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "naomi", "naomi2", "awbios", "naomigd", "hod2bios", "f355bios", "f355dlx", "airlbios" };
        foreach (var file in Directory.GetFiles(_paths.DreamcastDir, "*", SearchOption.AllDirectories).OrderBy(f => f))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            var fi = new FileInfo(file);
            string name = Path.GetFileNameWithoutExtension(fi.Name);
            GameInfo g;
            if (ext == ".dat" || ext == ".lst" || (ext == ".bin" && IsNaomiImage(file)))
            {
                // Naomi cart as one flat image. Named after its MAME set (<set>.dat), MAME supplies the title,
                // year, players and controls; otherwise the file name is the title.
                g = new GameInfo();
                if (ReadXml(g, name.ToLowerInvariant())) g.Set = "dc_" + g.Set;
                else
                {
                    string title = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
                    g.Set = "dc_" + Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", ""); g.Title = title;
                    g.Controls = new() { "joy8" };
                }
                g.Version = string.Join(", ", new[] { "Naomi arcade", g.Version }.Where(s => s != ""));
                g.Emulation = "";
            }
            else if (ext == ".zip" || ext == ".7z")
            {
                // Naomi / Atomiswave: MAME-format sets, so MAME knows the title, year and players
                if (biosZips.Contains(name)) continue;
                g = new GameInfo();
                if (!ReadXml(g, name.ToLowerInvariant())) { Log.Write($"dreamcast\\{fi.Name}: not a Naomi set MAME knows, skipped"); continue; }
                g.Version = string.Join(", ", new[] { "Naomi arcade", g.Version }.Where(s => s != ""));
                g.Emulation = "";       // MAME's own status for this board doesn't apply: Flycast runs it
                g.Set = "dc_" + g.Set;  // kept apart from any MAME copy of the same game
            }
            else if (DreamcastExts.Contains(ext))
            {
                // Naomi GD-ROM disc: <set>\gdl-xxxx.chd beside <set>.zip belongs to that zip, it isn't a Dreamcast disc
                var dir = Path.GetDirectoryName(file)!;
                if (File.Exists(dir + ".zip") || File.Exists(dir + ".7z")) continue;
                // "Some Game (USA)" -> title "Some Game", version "USA"
                var tags = Regex.Matches(name, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
                string title = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
                if (title == "" || Regex.IsMatch(title, @"^track\d*$", RegexOptions.IgnoreCase)) title = new DirectoryInfo(Path.GetDirectoryName(file)!).Name;
                g = new GameInfo
                {
                    Set = "dc_" + Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", ""), Title = title,
                    Version = string.Join(", ", tags.Prepend("Sega Dreamcast")), Controls = new() { "dcpad" },
                };
            }
            else continue;
            g.System = "dreamcast"; g.FilePath = file; g.Zip = Path.GetRelativePath(_paths.DreamcastDir, file);
            g.ZipSize = fi.Length; g.ZipTime = fi.LastWriteTimeUtc; g.Runnable = true; g.Missing.Clear(); g.Problem = null;
            if (g.Version.StartsWith("Naomi") && !File.Exists(Path.Combine(_paths.DreamcastDir, "naomi.zip")))
            {
                g.Runnable = false; g.Missing.Add("naomi.zip"); g.Problem = "needs the Naomi BIOS (dreamcast\\naomi.zip)";
            }
            if (list.Any(o => o.Set == g.Set)) continue; // same game twice (e.g. .chd and .gdi copies)
            list.Add(g);
            progress?.Invoke(g.Set.ToUpperInvariant(), "DC");
        }
        return list;
    }

    /// <summary>
    /// Sega Saturn discs in saturn\ (a folder each: "Some Game (USA)\Some Game (USA).cue" + its .bin),
    /// run by Mednafen. Needs the Saturn BIOS in emulators\mednafen\firmware (mpr-17933.bin US, sega_101.bin JP).
    /// </summary>
    List<GameInfo> ScanSaturn(Action<string, string>? progress)
    {
        var list = new List<GameInfo>();
        if (!Directory.Exists(_paths.SaturnDir) || !File.Exists(_paths.MednafenExe)) return list;
        bool bios = File.Exists(Path.Combine(_paths.MednafenDir, "firmware", "mpr-17933.bin"));
        foreach (var file in Directory.GetFiles(_paths.SaturnDir, "*", SearchOption.AllDirectories).OrderBy(f => f))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".cue" or ".chd" or ".ccd" or ".m3u")) continue;
            var fi = new FileInfo(file);
            string name = Path.GetFileNameWithoutExtension(fi.Name);
            var tags = Regex.Matches(name, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
            string title = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
            var g = new GameInfo
            {
                System = "saturn", FilePath = file, Zip = Path.GetRelativePath(_paths.SaturnDir, file), ZipSize = fi.Length, ZipTime = fi.LastWriteTimeUtc,
                Set = "ss_" + Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", ""), Title = title,
                Version = string.Join(", ", tags.Prepend("Sega Saturn")), Controls = new() { "sspad" }, Runnable = bios,
            };
            if (!bios) { g.Missing.Add("mpr-17933.bin"); g.Problem = "needs the Saturn BIOS (emulators\\mednafen\\firmware\\mpr-17933.bin)"; }
            if (list.Any(o => o.Set == g.Set)) continue;
            list.Add(g);
            progress?.Invoke(g.Set.ToUpperInvariant(), "SS");
        }
        return list;
    }

    static bool IsNaomiImage(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var b = new byte[5];
            return fs.Read(b, 0, 5) == 5 && System.Text.Encoding.ASCII.GetString(b) == "NAOMI";
        }
        catch { return false; }
    }

    void ApplyOverrides(List<GameInfo> games)
    {
        // Genres: catver.ini if there is one, then config\games.json wins.
        var catver = GameTags.LoadCatver(_paths.Catver);
        foreach (var g in games)
            g.Genre = catver.GetValueOrDefault(g.Set) ?? (g.CloneOf != "" ? catver.GetValueOrDefault(g.CloneOf) : null) ?? "OTHER";
        // config\games.json (optional): { "<set>": { "title": "...", "hidden": true, "genre": "Beat 'em up", "maker": "..." } }
        if (!File.Exists(_paths.GameOverrides)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_paths.GameOverrides), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var g in games)
            {
                // "characters": [ { "name": "LEONARDO", "color": "#2a5ad0" }, ... ] on the set, else on its parent
                foreach (var set in new[] { g.Set, g.CloneOf })
                {
                    if (set == "" || !doc.RootElement.TryGetProperty(set, out var co) || !co.TryGetProperty("characters", out var chs)
                        || chs.ValueKind != JsonValueKind.Array) continue;
                    g.Characters = chs.EnumerateArray().Select(c => new GameInfo.Character(
                        (c.TryGetProperty("name", out var n) ? n.GetString() : null) ?? "?",
                        c.TryGetProperty("color", out var cl) && cl.GetString() is string hex && uint.TryParse(hex.TrimStart('#'),
                            System.Globalization.NumberStyles.HexNumber, null, out uint rgb) ? 0xFF000000u | (rgb & 0xFFFFFF) : 0xFFA0A0A0u)).ToList();
                    break;
                }
                if (!doc.RootElement.TryGetProperty(g.Set, out var o)) continue;
                if (o.TryGetProperty("title", out var t)) g.Title = t.GetString() ?? g.Title;
                if (o.TryGetProperty("hidden", out var h)) g.Hidden = h.GetBoolean();
                if (o.TryGetProperty("genre", out var gn) && gn.GetString() is string gs && gs != "") g.Genre = gs.ToUpperInvariant();
                if (o.TryGetProperty("maker", out var mk) && mk.GetString() is string ms && ms != "") g.Maker = ms;
                if (o.TryGetProperty("version", out var vr) && vr.GetString() is string vs) g.Version = vs;
                if (o.TryGetProperty("year", out var yr) && yr.GetString() is string ys && ys != "") g.Year = ys;
                if (o.TryGetProperty("players", out var pl) && pl.TryGetInt32(out int pn)) g.Players = pn;
                if (o.TryGetProperty("crt", out var crt) && (crt.ValueKind == JsonValueKind.True || crt.ValueKind == JsonValueKind.False)) g.Crt = crt.GetBoolean();
                if (o.TryGetProperty("flycast", out var fc) && fc.ValueKind == JsonValueKind.Object)
                    foreach (var p in fc.EnumerateObject())
                        g.Flycast[p.Name] = p.Value.ValueKind switch
                        {
                            JsonValueKind.True => "yes", JsonValueKind.False => "no", JsonValueKind.String => p.Value.GetString()!, _ => p.Value.GetRawText(),
                        };
            }
        }
        catch (Exception ex) { Log.Write("games.json ignored: " + ex.Message); }
    }

    GameInfo Identify(FileInfo fi, long size, DateTime time, bool isDir)
    {
        var g = new GameInfo { Zip = fi.Name, ZipSize = size, ZipTime = time };
        string name = (isDir ? fi.Name : Path.GetFileNameWithoutExtension(fi.Name)).ToLowerInvariant();
        bool plainName = Regex.IsMatch(name, "^[a-z0-9_]+$");
        if (!plainName || !ReadXml(g, name))
        {
            // Not a MAME set name: ask MAME which set these ROMs belong to, then serve them under that name.
            string? set = isDir ? null : GuessSet(fi.FullName, name); // a folder has to be named after its set
            if (set != null && ReadXml(g, set))
            {
                g.Alias = set;
                EnsureAlias(fi.FullName, set);
                Log.Write($"{fi.Name} identified as MAME set '{set}'");
            }
            else
            {
                g.Set = name; g.Title = name; g.Hidden = true; g.Problem = "MAME doesn't recognise this file";
                Log.Write($"{fi.Name}: not a MAME set");
            }
        }
        return g;
    }

    void EnsureAlias(string zip, string set)
    {
        Directory.CreateDirectory(_paths.RomAlias);
        var dst = Path.Combine(_paths.RomAlias, set + Path.GetExtension(zip));
        var src = new FileInfo(zip);
        var d = new FileInfo(dst);
        if (!d.Exists || d.Length != src.Length) File.Copy(zip, dst, true);
    }

    string? GuessSet(string zip, string fileName)
    {
        var counts = new Dictionary<string, int>();
        foreach (var line in RunMame($"-romident \"{zip}\"").Split('\n'))
        {
            // "<file in zip>        = <rom name>            <set>     <description>"
            var m = Regex.Match(line, @"=\s+\S+\s+(\S+)\s");
            if (m.Success) counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
        }
        // Most matching ROMs wins; on a tie (a BIOS chip is in hundreds of sets) prefer a set named in the file name.
        return counts.Count == 0 ? null : counts.OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => fileName.Contains(kv.Key) ? kv.Key.Length : 0)
            .ThenBy(kv => kv.Key.Length).First().Key;
    }

    bool ReadXml(GameInfo g, string set)
    {
        string xml = RunMame($"-listxml {set}");
        int start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) return false;
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml[start..]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
                doc.Load(r);
            var m = doc.SelectSingleNode($"//machine[@name='{set}']");
            if (m == null) return false;
            g.Set = set;
            string desc = m.SelectSingleNode("description")?.InnerText ?? set;
            var vm = Regex.Match(desc, @"^(.*?)\s*\((.*)\)\s*$");
            g.Title = vm.Success ? vm.Groups[1].Value : desc;
            g.Version = vm.Success ? vm.Groups[2].Value : "";
            g.Year = m.SelectSingleNode("year")?.InnerText ?? "";
            g.Maker = m.SelectSingleNode("manufacturer")?.InnerText ?? "";
            g.Hidden = m.Attributes?["isbios"]?.Value == "yes" || m.Attributes?["isdevice"]?.Value == "yes";
            g.CloneOf = m.Attributes?["cloneof"]?.Value ?? "";
            var input = m.SelectSingleNode("input");
            g.Players = int.TryParse(input?.Attributes?["players"]?.Value, out int p) ? p : 1;
            g.Controls.Clear(); g.Buttons = 0;
            foreach (XmlNode c in input?.SelectNodes("control") ?? (XmlNodeList)doc.CreateElement("x").ChildNodes)
            {
                string type = c.Attributes?["type"]?.Value ?? "";
                if (type == "joy") type += c.Attributes?["ways"]?.Value ?? "8";
                if (!g.Controls.Contains(type)) g.Controls.Add(type);
                if (int.TryParse(c.Attributes?["buttons"]?.Value, out int b)) g.Buttons = Math.Max(g.Buttons, b);
            }
            var disp = m.SelectSingleNode("display");
            g.Vector = disp?.Attributes?["type"]?.Value == "vector";
            g.Rotate = int.TryParse(disp?.Attributes?["rotate"]?.Value, out int rot) ? rot : 0;
            g.Width = int.TryParse(disp?.Attributes?["width"]?.Value, out int dw) ? dw : 0;
            g.Height = int.TryParse(disp?.Attributes?["height"]?.Value, out int dh) ? dh : 0;
            g.Screens = Math.Max(1, m.SelectNodes("display")?.Count ?? 1);
            g.Emulation = m.SelectSingleNode("driver")?.Attributes?["status"]?.Value ?? "";
            return true;
        }
        catch (Exception ex) { Log.Write($"listxml {set}: {ex.Message}"); return false; }
    }

    public void Verify(GameInfo g)
    {
        string outp = RunMame($"-verifyroms {g.Set}");
        // A disc image (CHD) dropped loose into roms\ belongs in roms\<set>\: link it there and check again.
        bool linked = false;
        foreach (Match cm in Regex.Matches(outp, @"^\S+\s*:\s*(\S+)\s+-\s+NOT FOUND", RegexOptions.Multiline))
        {
            var loose = Path.Combine(_paths.Roms, cm.Groups[1].Value + ".chd");
            if (!File.Exists(loose)) continue;
            var dir = Path.Combine(_paths.RomAlias, g.Set);
            var dst = Path.Combine(dir, cm.Groups[1].Value + ".chd");
            Directory.CreateDirectory(dir);
            if (!File.Exists(dst) && !CreateHardLink(dst, loose, IntPtr.Zero)) File.Copy(loose, dst);
            Log.Write($"{g.Set}: linked loose {Path.GetFileName(loose)}");
            linked = true;
        }
        if (linked) outp = RunMame($"-verifyroms {g.Set}");
        g.Missing.Clear();
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.Trim();
            // "<set>       : <rom> (235 bytes) - NOT FOUND"   or a disc image: "<set>       : <chd name> - NOT FOUND"
            var m = Regex.Match(line, @"^\S+\s*:\s*(\S+)\s+(?:\((\d+) bytes\)\s+)?-\s+(.*)$");
            if (m.Success && !m.Groups[3].Value.Contains("NO GOOD DUMP KNOWN") && !m.Groups[3].Value.StartsWith("OK"))
            {
                string why = m.Groups[3].Value.Contains("NOT FOUND") ? "missing" : m.Groups[3].Value.Contains("LENGTH") ? "wrong size" : "bad dump";
                var bios = Regex.Match(m.Groups[3].Value, @"\((\w+)\)");
                g.Missing.Add($"{m.Groups[1].Value} {why}" + (bios.Success ? $" (from {bios.Groups[1].Value})" : ""));
            }
        }
        g.Runnable = Regex.IsMatch(outp, @"romset \S+ (\[\S+\] )?is (good|best available)");
        // -verifyroms checks every alternative BIOS. If only ones the game doesn't boot with are missing, it still runs
        // (e.g. a set missing only its "japan" BIOS still boots on its default one).
        if (!g.Runnable && g.Missing.Count > 0 && OnlyOptionalBiosMissing(g, outp))
        {
            Log.Write($"verify {g.Set}: only alternative BIOS files missing ({string.Join(", ", g.Missing)}), runs anyway");
            g.Runnable = true; g.Missing.Clear();
        }
        // Every file there and the right size, but some checksums differ (other dumps, PAL/PROM files): MAME still runs these,
        // so they're playable; the panel says so. Missing or wrong-size files still stop a game.
        g.Problem = null;
        if (!g.Runnable && g.Missing.Count > 0 && g.Missing.All(x => x.EndsWith(" bad dump")))
        {
            Log.Write($"verify {g.Set}: checksums differ ({string.Join(", ", g.Missing)}), runs anyway");
            g.Runnable = true; g.Problem = "checksums"; g.Missing.Clear();
        }
        if (!g.Runnable && g.Missing.Count == 0) g.Missing.Add("MAME couldn't verify this set");
        Log.Write($"verify {g.Set}: {(g.Runnable ? "OK" : "BAD " + string.Join(", ", g.Missing))}");
    }

    bool OnlyOptionalBiosMissing(GameInfo g, string verifyOut)
    {
        string xml = RunMame($"-listxml {g.Set}");
        int start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) return false;
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml[start..]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
                doc.Load(r);
            var m = doc.SelectSingleNode($"//machine[@name='{g.Set}']");
            if (m == null) return false;
            string? defBios = m.SelectSingleNode("biosset[@default='yes']")?.Attributes?["name"]?.Value
                ?? m.SelectSingleNode("biosset")?.Attributes?["name"]?.Value; // MAME boots the first one when none is marked
            if (defBios == null) return false;
            foreach (Match mm in Regex.Matches(verifyOut, @"^\S+\s*:\s*(\S+)\s+(?:\(\d+ bytes\)\s+)?-\s+NOT FOUND", RegexOptions.Multiline))
            {
                string file = mm.Groups[1].Value;
                var rom = m.SelectNodes("rom")?.Cast<XmlNode>().FirstOrDefault(n => n.Attributes?["name"]?.Value == file);
                string? bios = rom?.Attributes?["bios"]?.Value;
                if (bios == null || bios == defBios) return false; // a file the game needs
            }
            return true;
        }
        catch (Exception ex) { Log.Write($"bios check {g.Set}: {ex.Message}"); return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CreateHardLink(string link, string existing, IntPtr sec);

    public string RunMame(string args)
    {
        var psi = new ProcessStartInfo(_paths.MameExe, args)
        {
            WorkingDirectory = _paths.MameDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        return o + "\n" + err.Result;
    }
}
