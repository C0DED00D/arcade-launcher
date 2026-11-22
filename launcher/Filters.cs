using System.Text.RegularExpressions;

namespace ArcadeLauncher;

/// <summary>How the menu sorts games under headers (X cycles the modes): genre, maker, decade, players, controls.</summary>
static class GameTags
{
    /// <summary>"" = one A-Z list with no headers.</summary>
    public static readonly string[] GroupModes = { "genre", "maker", "decade", "players", "controls", "" };

    public static string GroupModeName(string by) => by switch
    {
        "genre" => "genre", "maker" => "maker", "decade" => "decade", "players" => "players", "controls" => "controls", _ => "A-Z",
    };

    public static IEnumerable<string> ControlGroups(GameInfo g)
    {
        foreach (var c in g.Controls)
        {
            string? grp = c switch
            {
                _ when c.StartsWith("joy") || c == "doublejoy" => "Joystick",
                "stick" => "Analog yoke",
                "dial" or "paddle" => "Spinner",
                "trackball" => "Trackball",
                "positional" => "Wheel",
                "pedal" => "Pedal",
                "lightgun" => "Light gun",
                "jaguar" or "dcpad" or "sspad" => "Gamepad",
                _ => null,
            };
            if (grp != null) yield return grp;
        }
    }

    /// <summary>"Atari Games", "SNK (Nazca license)", "Sega / Westone" -> Atari, SNK, Sega.</summary>
    public static string MakerName(GameInfo g)
    {
        string m = g.Maker.Trim();
        if (m.Length == 0) return "Other";
        // "Cave (Capcom license)" -> Capcom: the name on the cabinet. But not a regional branch that only distributed it
        // ("Capcom (Data East USA license)" stays Capcom).
        var lic = Regex.Match(m, @"\(([^)]*?)\s+license\)", RegexOptions.IgnoreCase);
        if (lic.Success && !Regex.IsMatch(lic.Groups[1].Value, @"\b(USA|America|Europe|UK|Deutschland|Korea|Taiwan|Hong Kong)\b", RegexOptions.IgnoreCase))
            m = lic.Groups[1].Value;
        m = Regex.Split(m, @"\s*[/(,]\s*")[0];                 // first company only
        for (int i = 0; i < 3; i++) // "Irem America Corp." -> "Irem"
            m = Regex.Replace(m, @"\s+(Games|Corporation|Corp\.?|Co\.,? Ltd\.?|Ltd\.?|Inc\.?|Manufacturing|Electronics|license|America|of America|USA|Europe|Japan)$", "", RegexOptions.IgnoreCase).Trim();
        return m.ToUpperInvariant() switch
        {
            "NAZCA" or "SNK PLAYMORE" or "PLAYMORE" or "SAURUS" => "SNK",
            "BALLY MIDWAY" or "MIDWAY" or "BALLY/MIDWAY" => "Midway",
            "WILLIAMS ELECTRONICS" => "Williams",
            "LLAMASOFT" => "Atari",
            _ => m,
        };
    }

    /// <summary>Menu genre (kept upper case: "BEAT 'EM UP") as shown: "Beat 'em up".</summary>
    public static string GenreName(string genre) =>
        genre.Length == 0 ? "Other" : char.ToUpperInvariant(genre[0]) + genre[1..].ToLowerInvariant();

    static string PlayersName(int n) => n <= 0 ? "Unknown" : n == 1 ? "1 player" : $"{n} players";

    /// <summary>"1994" -> "1990s". MAME writes unsure years as "199?" or "19??".</summary>
    static string DecadeName(string year) =>
        year.Length >= 3 && char.IsDigit(year[0]) && char.IsDigit(year[1]) && char.IsDigit(year[2]) ? year[..3] + "0s" : "Unknown";

    public static IEnumerable<string> GroupKeys(GameInfo g, string by) => by switch
    {
        "genre" => new[] { GenreName(g.Genre) },
        "maker" => new[] { MakerName(g) },
        "decade" => new[] { DecadeName(g.Year) },
        "players" => new[] { PlayersName(g.Players) },
        "controls" => ControlGroups(g).Distinct().DefaultIfEmpty("Other"),
        _ => new[] { "" },
    };

    /// <summary>The groups in menu order, each with its games in the list's own order. A game can be in more than one (controls).</summary>
    public static List<(string Name, List<GameInfo> Games)> Group(List<GameInfo> games, string by)
    {
        var map = new Dictionary<string, List<GameInfo>>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var g in games)
            foreach (var k in GroupKeys(g, by))
            {
                if (!map.TryGetValue(k, out var l)) { map[k] = l = new(); names.Add(k); }
                if (!l.Contains(g)) l.Add(g);
            }
        static bool Last(string n) => n is "Other" or "Unknown";
        var order = by == "players"
            ? names.OrderBy(n => Last(n) ? 99 : int.Parse(n.Split(' ')[0]))
            : names.OrderBy(n => Last(n) ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
        return order.Select(n => (n, map[n])).ToList();
    }

    /// <summary>Short name for the list: "Some Game II / Other Region Title - ..." -> "Some Game II".</summary>
    public static string ShortTitle(string title)
    {
        var t = Regex.Split(title, @"\s+/\s+")[0];
        t = Regex.Split(t, @"\s+-\s+")[0].Trim();
        // "The Some Game" -> "Some Game, The": lists it under K, where you'd look for it
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && t.Length > 4) t = t[4..].TrimStart() + ", " + t[..3];
        return t;
    }

    // ------------------------------------------------------------------ genres

    /// <summary>Genres from catver.ini (if present: emulators\mame\catver.ini or config\catver.ini), mapped to a short list.</summary>
    public static Dictionary<string, string> LoadCatver(params string[] paths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = paths.FirstOrDefault(File.Exists);
        if (file == null) return map;
        bool inCat = false;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inCat = line.Equals("[Category]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inCat || line.Length == 0 || line.StartsWith(';')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = FriendlyGenre(line[(eq + 1)..].Trim());
        }
        Log.Write($"genres: {map.Count} from {file}");
        return map;
    }

    /// <summary>catver.ini category ("Platform / Shooter Scrolling", "Fighter / Versus") -> menu genre.</summary>
    public static string FriendlyGenre(string cat)
    {
        // e.g. "Fighter / 2.5D" (belt scrollers), "Fighter / Versus" (one-on-one), "Platform / Shooter Scrolling"
        // (run and gun), "Platform / Fighter Scrolling" (side-scrolling action), "Shooter / Gun" (light gun games)
        string c = cat.Replace("* Mature *", "").Trim();
        int slash = c.IndexOf(" / ", StringComparison.Ordinal);
        string main = (slash < 0 ? c : c[..slash]).Trim(), sub = slash < 0 ? "" : c[(slash + 3)..].Trim();
        bool subHas(string s) => sub.Contains(s, StringComparison.OrdinalIgnoreCase);
        switch (main.ToLowerInvariant())
        {
            case "fighter":
                if (subHas("2.5D") || subHas("Scrolling")) return "BEAT 'EM UP";
                return subHas("Field") ? "ACTION" : "FIGHTING";  // Field: arena games
            case "platform":
                // "Fighter Scrolling" is run-and-jump action: still platformers
                return subHas("Shooter") ? "RUN & GUN" : "PLATFORM";
            case "climbing": return "PLATFORM";
            case "shooter":
                if (subHas("Gun")) return "LIGHT GUN";
                if (subHas("Walking")) return "RUN & GUN";
                return "SHOOTER";
            case "driving": case "racing": return "DRIVING";
            case "sports": return "SPORTS";
            case "puzzle": return "PUZZLE";
            case "maze": return "MAZE";
            case "ball & paddle": case "breakout": return "BREAKOUT";
            default: return "OTHER";
        }
    }
}
