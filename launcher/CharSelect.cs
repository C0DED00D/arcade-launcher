namespace ArcadeLauncher;

/// <summary>
/// Character select, before a game whose player slots are fixed characters (config\games.json "characters": P1 is
/// always Leonardo, and so on). Every player's own controls (input.json Mame.Ports P1..P4) get a cursor: P1 is always
/// in, the others join with button 1 or Start. Stick left / right picks, button 1 or Start = ready, button 2 = undo
/// (before ready: P2-P4 leave again, P1 goes back to the menu). Two players can't be ready on one character. Once
/// everyone who joined is ready, a 3 second countdown (anyone joining stops it; a ready player pressing again skips
/// it), then the launcher writes each player's controls onto their character's slot in MAME's
/// ctrlr file (MameCtrlr.Write) and starts the game: MAME itself never knows.
/// </summary>
sealed class CharSelect
{
    public enum Result { None, Done, Cancel }

    public readonly GameInfo Game;
    public readonly int Sides;                  // players who can pick: one per character, at most 4
    public readonly int[] Cur;                  // [side] = character index (0-based)
    public readonly bool[] Joined, Ready;
    public readonly double[] Flash;             // [side] = when it last joined / got ready (for the screen)
    public readonly double OpenedAt;
    public Action<string>? Sound;               // "move", "toggle", "back", "buzz"

    readonly (InputBindings L, InputBindings R, InputBindings Ok, InputBindings Back)[] _b;
    readonly bool[,] _was;
    readonly Queue<(double At, int Side, int What)> _script = new();
    const int L = 0, R = 1, Ok = 2, Back = 3;

    public CharSelect(GameInfo g, InputConfig cfg, double now)
    {
        Game = g;
        Sides = Math.Min(4, g.Characters.Count);
        Cur = new int[Sides]; Joined = new bool[Sides]; Ready = new bool[Sides]; Flash = new double[Sides];
        OpenedAt = now;
        var ports = MameCtrlr.EffectivePorts(cfg, g);
        List<string> P(string type) => MameCtrlr.ForLauncher(ports.GetValueOrDefault(type) ?? new(), cfg).ToList();
        _b = new (InputBindings, InputBindings, InputBindings, InputBindings)[Sides];
        for (int s = 0; s < Sides; s++)
        {
            int p = s + 1;
            _b[s] = (InputBindings.From(P($"P{p}_JOYSTICK_LEFT"), $"P{p} left"), InputBindings.From(P($"P{p}_JOYSTICK_RIGHT"), $"P{p} right"),
                InputBindings.From(P($"P{p}_BUTTON1").Concat(P($"START{p}")), $"P{p} ready"), InputBindings.From(P($"P{p}_BUTTON2"), $"P{p} back"));
            Cur[s] = s;
        }
        Joined[0] = true;
        // whatever is held now (the Start that launched the game) only counts once it's let go
        _was = new bool[Sides, 4];
        for (int s = 0; s < Sides; s++) for (int k = 0; k < 4; k++) _was[s, k] = true;
    }

    /// <summary>Testing (--cs "1R,1R,2OK,..."): presses made one every quarter second from now.</summary>
    public void Script(IEnumerable<string> steps, double now)
    {
        double at = now + 0.8;
        foreach (var st in steps)
        {
            if (st.Length < 2 || !char.IsDigit(st[0])) continue;
            int what = st[1..].ToUpperInvariant() switch { "L" => L, "R" => R, "OK" => Ok, "BACK" => Back, _ => -1 };
            if (what >= 0) _script.Enqueue((at += 0.25, st[0] - '1', what));
        }
    }

    /// <summary>The character side s is on, if it's ready there.</summary>
    int? LockedBy(int ch, int except = -1)
    {
        for (int s = 0; s < Sides; s++) if (s != except && Joined[s] && Ready[s] && Cur[s] == ch) return s;
        return null;
    }
    public int? LockedOn(int ch) => LockedBy(ch);

    void Step(int s, int d)
    {
        int n = Game.Characters.Count, c = Cur[s];
        for (int i = 0; i < n; i++) { c = (c + d + n) % n; if (LockedBy(c, s) == null) break; }
        Cur[s] = c;
    }

    void Join(int s, double now)
    {
        Joined[s] = true; Ready[s] = false;
        // a character nobody is on, else one nobody is ready on
        int? best = null;
        for (int i = 0; i < Game.Characters.Count; i++)
        {
            if (LockedBy(i, s) != null) continue;
            bool alone = !Enumerable.Range(0, Sides).Any(o => o != s && Joined[o] && Cur[o] == i);
            if (alone) { best = i; break; }
            best ??= i;
        }
        Cur[s] = best ?? 0;
        Flash[s] = now;
    }

    public Result Update(double now)
    {
        var fake = new bool[Sides, 4];
        while (_script.Count > 0 && _script.Peek().At <= now) { var (_, s, w) = _script.Dequeue(); if (s < Sides) fake[s, w] = true; }
        for (int s = 0; s < Sides; s++)
        {
            bool Edge(int k, InputBindings b)
            {
                bool real = b.IsDown(GameInput.Key);
                bool e = fake[s, k] || (real && !_was[s, k]);
                _was[s, k] = real;
                return e;
            }
            var (bl, br, bok, bback) = _b[s];
            bool l = Edge(L, bl), r = Edge(R, br), ok = Edge(Ok, bok), back = Edge(Back, bback);
            if (!Joined[s])
            {
                if (ok) { Join(s, now); Sound?.Invoke("toggle"); }
            }
            else if (!Ready[s])
            {
                if (l) { Step(s, -1); Sound?.Invoke("move"); }
                if (r) { Step(s, 1); Sound?.Invoke("move"); }
                if (ok)
                {
                    if (LockedBy(Cur[s], s) == null) { Ready[s] = true; Flash[s] = now; Sound?.Invoke("toggle"); }
                    else Sound?.Invoke("buzz");
                }
                if (back)
                {
                    if (s == 0) return Result.Cancel;
                    Joined[s] = false; Sound?.Invoke("back");
                }
            }
            else if (back) { Ready[s] = false; Sound?.Invoke("back"); }
            else if (ok && GoAt != null) skip = true;
        }
        // everyone who joined is ready: a short countdown, so the others can still join (which stops it); a ready
        // player pressing again skips it
        bool all = Enumerable.Range(0, Sides).All(s => !Joined[s] || Ready[s]);
        if (!all) { GoAt = null; skip = false; return Result.None; }
        GoAt ??= now + Countdown;
        return skip || now >= GoAt ? Result.Done : Result.None;
    }

    public const double Countdown = 3;
    /// <summary>When the game starts, once everyone who joined is ready (null: not everyone is).</summary>
    public double? GoAt { get; private set; }
    bool skip;

    /// <summary>Who sits where: [slot] = the player (1-based both) whose controls drive it, and who joined.</summary>
    public Seating Seats()
    {
        var bySlot = new Dictionary<int, int>();
        var joined = new HashSet<int>();
        for (int s = 0; s < Sides; s++)
            if (Joined[s]) { bySlot[Cur[s] + 1] = s + 1; joined.Add(s + 1); }
        return new Seating(bySlot, joined);
    }
}

/// <summary>
/// The result of character select. SideOfSlot[slot] = the player whose controls drive that slot. A slot nobody chose is
/// switched off if its own player joined (their controls went elsewhere); a player who didn't join keeps their own slot
/// unless someone chose it.
/// </summary>
sealed record Seating(Dictionary<int, int> SideOfSlot, HashSet<int> Joined)
{
    /// <summary>
    /// Slots (of a game with this many) that nobody plays and that get no controls: an unchosen slot whose own player
    /// joined, and every unchosen slot past 4 (no player owns those: MAME's own P5+ mapping would reuse P3/P4's devices).
    /// </summary>
    public IEnumerable<int> Off(int slots) =>
        Enumerable.Range(1, Math.Max(4, slots)).Where(s => !SideOfSlot.ContainsKey(s) && (Joined.Contains(s) || s > 4));

    public override string ToString() =>
        string.Join(", ", SideOfSlot.OrderBy(kv => kv.Value).Select(kv => $"P{kv.Value} -> slot {kv.Key}"));
}
