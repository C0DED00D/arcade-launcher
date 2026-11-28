namespace ArcadeLauncher;

/// <summary>Draws the menu into the canvas (16:9, shown through the CRT shader). Holds no game logic beyond what to show.</summary>
class MenuScreen
{
    public const int W = 1280, H = 720;
    public readonly Canvas C = new(W, H);
    readonly PixelFont _f;
    readonly Paths _paths;
    readonly (float x, float y, float speed, int shade)[] _stars;
    readonly Dictionary<string, List<Img>> _snapCache = new();
    readonly Random _rng = new(7);

    static readonly uint White = Canvas.Rgb(240, 240, 240), Grey = Canvas.Rgb(140, 140, 150), Dim = Canvas.Rgb(80, 80, 92),
        Yellow = Canvas.Rgb(255, 214, 40), Red = Canvas.Rgb(240, 60, 50), Cyan = Canvas.Rgb(60, 220, 255), Black = Canvas.Rgb(0, 0, 0),
        Shadow = Canvas.Rgb(10, 10, 30);

    public MenuScreen(Paths paths)
    {
        _paths = paths;
        _f = new PixelFont(paths.Font);
        _stars = Enumerable.Range(0, 160).Select(_ => ((float)_rng.NextDouble() * W, (float)_rng.NextDouble() * H, 4 + (float)_rng.NextDouble() * 40, _rng.Next(40, 160))).ToArray();
    }

    public void ForgetSnaps(string set) => _snapCache.Remove(set);

    List<Img> Snaps(string set)
    {
        if (_snapCache.TryGetValue(set, out var l)) return l;
        l = new List<Img>();
        foreach (var f in SnapshotJob.SnapsFor(_paths, set)) { try { l.Add(Img.Load(f)); } catch { } }
        if (l.Count > 0) _snapCache[set] = l; // empty result isn't cached, so new snapshots show up
        return l;
    }

    // ---------------------------------------------------------------- boot self-test

    public void DrawBoot(List<(string name, string result)> lines, double t, bool done)
    {
        C.Clear(Black);
        _f.Draw(C, "ARCADE SYSTEM", 64, 40, 3, White);
        _f.Draw(C, "ROM / RAM TEST", 64, 84, 2, Grey);
        int y = 130;
        int first = Math.Max(0, lines.Count - 18);
        for (int i = first; i < lines.Count; i++)
        {
            var (name, result) = lines[i];
            string label = name.Length > 30 ? name[..30] : name;
            _f.Draw(C, label, 64, y, 2, White);
            int dotsX = 64 + _f.Measure(label, 2) + 8;
            for (int x = dotsX; x < 880; x += 12) C.Fill(x, y + 16, 3, 3, Dim);
            uint col = result == "OK" ? Canvas.Rgb(80, 255, 120) : result == "BAD" ? Red : Grey;
            _f.Draw(C, result, 900, y, 2, col);
            y += 28;
        }
        if (!done && ((int)(t * 3) & 1) == 0) C.Fill(64, y + 4, 14, 20, White); // blinking cursor
        if (done) _f.Draw(C, "TEST COMPLETE", 64, y + 8, 2, Yellow);
    }

    // ---------------------------------------------------------------- character select

    static readonly uint[] PlayerCol = { Canvas.Rgb(70, 130, 255), Canvas.Rgb(255, 70, 70), Canvas.Rgb(60, 210, 90), Canvas.Rgb(255, 200, 40) };

    /// <summary>One card per character, a cursor and a status panel per player (CharSelect).</summary>
    public void DrawCharSelect(CharSelect cs, double t)
    {
        DrawBackground(t);
        double since = t - cs.OpenedAt;
        var chars = cs.Game.Characters;
        int n = chars.Count;
        float pulse = (float)(0.5 + 0.5 * Math.Sin(t * 5));

        // heading: the game, then a bobbing rainbow title like the menu's logo
        _f.DrawCentered(C, Fit(GameTags.ShortTitle(cs.Game.Title).ToUpperInvariant(), W - 80, 2), W / 2, 14, 2, Grey, 1, Shadow);
        const string title = "SELECT YOUR CHARACTER";
        int tx0 = W / 2 - _f.Measure(title, 4) / 2;
        for (int i = 0; i < title.Length; i++)
        {
            string ch = title[i].ToString();
            int bob = (int)Math.Round(Math.Sin(t * 2.6 + i * 0.45) * 2);
            uint col = Canvas.Mix(Canvas.Hsv((float)(t * 0.1 + i * 0.035), 0.7f, 1f), White, 0.15f);
            _f.Draw(C, ch, tx0 + 3, 42 + bob + 3, 4, Shadow);
            _f.Draw(C, ch, tx0, 42 + bob, 4, col);
            tx0 += _f.Measure(ch, 4);
        }
        for (int px = 160; px < W - 160; px++)
        {
            float edge = Math.Min(px - 160, W - 160 - px) / 120f;
            C.Plot(px, 98, Canvas.Hsv((float)(px / 900.0 - t * 0.15), 0.8f, 0.9f), Math.Min(1, edge));
        }

        // character cards
        const int cardY = 126, cardH = 316, gap = 28, margin = 48;
        int cardW = Math.Min(270, (W - 2 * margin - gap * (n - 1)) / n);
        int x0 = (W - (n * cardW + (n - 1) * gap)) / 2;
        for (int i = 0; i < n; i++)
        {
            var chr = chars[i];
            uint col = chr.Color;
            var on = Enumerable.Range(0, cs.Sides).Where(s => cs.Joined[s] && cs.Cur[s] == i).ToList();
            int? lockS = cs.LockedOn(i);
            bool lit = on.Count > 0;
            int cx = x0 + i * (cardW + gap);
            double enter = Math.Clamp((since - i * 0.07) / 0.4, 0, 1);       // cards rise into place in turn
            int cy = cardY + (int)(Math.Pow(1 - enter, 3) * 260) - (lit && lockS == null ? (int)Math.Round(4 + 3 * Math.Sin(t * 4)) : 0);

            if (lit)
            {
                uint gc = lockS is int gl ? PlayerCol[gl] : col;
                float strength = lockS != null ? 0.55f : 0.3f + 0.25f * pulse;
                for (int k = 1; k <= 12; k++) FrameA(cx - k, cy - k, cardW + 2 * k, cardH + 2 * k, gc, strength * (1 - k / 13f) * 0.6f);
            }
            uint top = Canvas.Mix(Canvas.Rgb(8, 8, 18), col, lit ? 0.85f : 0.35f), bottom = Canvas.Rgb(6, 6, 14);
            for (int yy = 0; yy < cardH; yy++) C.Fill(cx, cy + yy, cardW, 1, Canvas.Mix(top, bottom, MathF.Pow(yy / (float)cardH, 0.75f)));

            // emblem: a ring with the character's initial
            int rcx = cx + cardW / 2, rcy = cy + 116, rad = Math.Min(cardW / 2 - 20, 90);
            Disc(rcx, rcy, rad, Canvas.Mix(col, White, lit ? 0.2f : 0f), lit ? 0.5f : 0.22f);
            Disc(rcx, rcy, rad - 7, Black, 0.55f);
            int sc = Math.Clamp(rad / 8, 3, 12);
            uint ic = lit ? Canvas.Mix(col, White, 0.45f) : Canvas.Mix(col, Grey, 0.55f);
            _f.DrawCentered(C, chr.Name[..1], rcx + sc / 2, rcy - 6 * sc, sc, ic, 1, Canvas.Mix(col, Black, 0.75f));

            // a sheen sweeping across the card a cursor is on
            if (lit && lockS == null)
            {
                int band = (int)(t * 300 % (cardW + cardH + 300)) - 150;
                for (int yy = 0; yy < cardH; yy++)
                {
                    int bx = Math.Max(cx, cx + band - yy / 2), bw = Math.Min(cx + cardW, cx + band - yy / 2 + 28) - bx;
                    if (bw > 0) C.Fill(bx, cy + yy, bw, 1, White, 0.09f);
                }
            }

            // name plate
            int py = cy + cardH - 62;
            C.Fill(cx, py, cardW, 62, Black, 0.55f);
            C.Fill(cx, py, cardW, 3, col);
            int ns = _f.Measure(chr.Name, 3) <= cardW - 16 ? 3 : 2;
            _f.DrawCentered(C, Fit(chr.Name, cardW - 12, ns), cx + cardW / 2, py + (62 - 11 * ns) / 2 + 2, ns, lit ? White : Grey, 1, Shadow);
            C.Frame(cx, cy, cardW, cardH, lockS is int fl ? PlayerCol[fl] : lit ? Canvas.Mix(col, White, 0.35f + 0.5f * pulse) : Canvas.Rgb(50, 50, 70), 3);

            // taken: a banner in the player's colour, flashing white as it lands
            if (lockS is int lk)
            {
                float f = (float)Math.Clamp(1 - (t - cs.Flash[lk]) * 3, 0, 1);
                int by = cy + 196;
                C.Fill(cx, by, cardW, 44, Canvas.Mix(PlayerCol[lk], White, f));
                C.Fill(cx, by + 44, cardW, 3, Black, 0.45f);
                string txt = $"P{lk + 1} READY";
                int s3 = _f.Measure(txt, 3) <= cardW - 16 ? 3 : 2;
                _f.DrawCentered(C, txt, cx + cardW / 2, by + (44 - 11 * s3) / 2 + 2, s3, White, 1, Shadow);
            }

            // the players on this card: an arrow and a tag each
            const int tagW = 58, tagGap = 8;
            int tagX = cx + (cardW - (on.Count * tagW + (on.Count - 1) * tagGap)) / 2;
            foreach (int s in on)
            {
                int ty = cardY + cardH + 12 + (cs.Ready[s] ? 0 : (int)Math.Round(3 * Math.Sin(t * 7 + s * 1.3)));
                for (int a = 0; a < 8; a++) C.Fill(tagX + tagW / 2 - a, ty + a, 2 * a + 1, 1, PlayerCol[s]);
                int tby = ty + 9;
                if (cs.Ready[s]) C.Fill(tagX, tby, tagW, 30, PlayerCol[s]);
                else { C.Fill(tagX, tby, tagW, 30, Black, 0.8f); C.Frame(tagX, tby, tagW, 30, PlayerCol[s], 2); }
                _f.DrawCentered(C, $"P{s + 1}", tagX + tagW / 2, tby + 7, 2, White, 1, Shadow);
                tagX += tagW + tagGap;
            }
        }

        // one panel per player
        const int panY = 524, panH = 56, tagw = 64;
        int pw = Math.Min(300, (W - 2 * margin - gap * (cs.Sides - 1)) / cs.Sides);
        int px0 = (W - (cs.Sides * pw + (cs.Sides - 1) * gap)) / 2;
        for (int s = 0; s < cs.Sides; s++)
        {
            int px = px0 + s * (pw + gap);
            uint pc = PlayerCol[s];
            string label; uint tc;
            if (!cs.Joined[s])
            {
                C.Fill(px, panY, pw, panH, Black, 0.6f);
                C.Frame(px, panY, pw, panH, Canvas.Mix(pc, Black, 0.55f), 2);
                label = "PRESS START"; tc = ((int)(t * 2) & 1) == 0 ? White : Dim;
            }
            else if (cs.Ready[s])
            {
                C.Fill(px, panY, pw, panH, Canvas.Mix(pc, Black, 0.3f));
                C.Frame(px, panY, pw, panH, Canvas.Mix(pc, White, 0.5f), 2);
                label = chars[cs.Cur[s]].Name; tc = White;
            }
            else
            {
                C.Fill(px, panY, pw, panH, Canvas.Mix(pc, Black, 0.82f));
                C.Frame(px, panY, pw, panH, pc, 2);
                label = "CHOOSING"; tc = Canvas.Mix(White, pc, pulse * 0.6f);
            }
            C.Fill(px, panY, tagw, panH, cs.Joined[s] ? pc : Canvas.Mix(pc, Black, 0.5f));
            _f.DrawCentered(C, $"P{s + 1}", px + tagw / 2, panY + (panH - 33) / 2 + 1, 3, White, 1, Shadow);
            _f.DrawCentered(C, Fit(label, pw - tagw - 14, 2), px + tagw + (pw - tagw) / 2, panY + (panH - 22) / 2 + 2, 2, tc, 1, Shadow);
        }
        if (cs.GoAt is double go)
        {
            int left = Math.Max(1, (int)Math.Ceiling(go - t));
            float beat = (float)((go - t) % 1);
            string msg = $"STARTING IN {left}";
            int bw = _f.Measure(msg, 3) + 48;
            C.Fill((W - bw) / 2, 596, bw, 44, Black, 0.75f);
            C.Frame((W - bw) / 2, 596, bw, 44, Canvas.Mix(Yellow, White, beat), 2);
            _f.DrawCentered(C, msg, W / 2, 604, 3, Canvas.Mix(Yellow, White, beat), 1, Shadow);
        }
        else _f.DrawCentered(C, "The game starts when everyone who joined is ready", W / 2, 606, 2, Grey, 1, Shadow);

        const int fy = 668;
        C.Fill(0, fy, W, H - fy, Black, 0.6f);
        for (int px = 0; px < W; px++) C.Plot(px, fy, Canvas.Rgb(50, 50, 70));
        _f.Draw(C, "Stick: choose    Button 1 / Start: join, ready    Button 2: back    P1 button 2: menu", 24, fy + 16, 2, Grey);
    }

    /// <summary>A one-pixel outline, blended.</summary>
    void FrameA(int x, int y, int w, int h, uint c, float a)
    {
        C.Fill(x, y, w, 1, c, a); C.Fill(x, y + h - 1, w, 1, c, a);
        C.Fill(x, y + 1, 1, h - 2, c, a); C.Fill(x + w - 1, y + 1, 1, h - 2, c, a);
    }

    void Disc(int cx, int cy, int r, uint c, float a)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            int dx = (int)Math.Sqrt(r * r - dy * dy);
            C.Fill(cx - dx, cy + dy, 2 * dx + 1, 1, c, a);
        }
    }

    // ---------------------------------------------------------------- main menu

    public void DrawMenu(MenuModel m, double t)
    {
        DrawBackground(t);
        DrawLogo(t);
        DrawList(m, t);
        var row = m.Row;
        if (row.Game != null) DrawPanel(m, row.Game, t);
        else if (row.IsHeader) DrawGroupPanel(m, row, t);
        else DrawExitPanel(t);
        DrawHeader(m);
        DrawFooter(m, t);
        if (m.Toast != null && t < m.ToastUntil) DrawToast(m.Toast);
        if (m.ConfirmExit) DrawConfirm(t);
    }

    /// <summary>Either side of the logo: where you are in the list, and how it's sorted.</summary>
    void DrawHeader(MenuModel m)
    {
        string pos;
        if (m.All.Count == 0) pos = "No games";
        else if (m.GroupBy == "")
            pos = m.Selected != null ? $"{m.Index + 1} / {m.All.Count}" : $"{m.All.Count} games";
        else if (m.Row.Game != null && m.Row.Group != null)
        {
            var grp = m.Groups.First(gr => gr.Name == m.Row.Group).Games;
            pos = $"{m.Row.Group}  {grp.IndexOf(m.Row.Game) + 1} / {grp.Count}";
        }
        else pos = $"{m.All.Count} games, {m.Groups.Count} groups";
        _f.Draw(C, Fit(pos, 440, 2), ListX + 2, 22, 2, Grey, 1, Shadow);

        string by = m.GroupBy == "" ? "A-Z" : "By " + GameTags.GroupModeName(m.GroupBy);
        string s = by + $"    {Label("Menu", "Filter")}: change";
        int x = W - 24 - _f.Measure(s, 2);
        _f.Draw(C, by, x, 22, 2, Cyan, 1, Shadow);
        _f.Draw(C, $"    {Label("Menu", "Filter")}: change", x + _f.Measure(by, 2), 22, 2, Dim);
    }

    void DrawBackground(double t)
    {
        // Deep blue gradient with a slow starfield
        for (int y = 0; y < H; y++)
        {
            float k = y / (float)H;
            C.Fill(0, y, W, 1, Canvas.Rgb((int)(4 + 10 * k), (int)(2 + 6 * k), (int)(18 + 26 * k)));
        }
        foreach (var s in _stars)
        {
            float x = (float)((s.x - t * s.speed) % W); if (x < 0) x += W;
            C.Plot((int)x, (int)s.y, Canvas.Rgb(s.shade, s.shade, s.shade + 30));
        }
    }

    void DrawLogo(double t)
    {
        const string logo = "ARCADE";
        int scale = 4;
        int x = W / 2 - _f.Measure(logo, scale) / 2;
        for (int i = 0; i < logo.Length; i++)
        {
            string ch = logo[i].ToString();
            int bob = (int)Math.Round(Math.Sin(t * 2.2 + i * 0.7) * 2);
            uint col = Canvas.Hsv((float)(t * 0.08 + i * 0.09), 0.75f, 1f);
            _f.Draw(C, ch, x + 3, 8 + bob + 3, scale, Shadow);
            _f.Draw(C, ch, x, 8 + bob, scale, col);
            x += _f.Measure(ch, scale);
        }
        // Rainbow rule under the logo
        for (int px = 24; px < W - 24; px++) C.Plot(px, 62, Canvas.Hsv((float)(px / 900.0 - t * 0.15), 0.8f, 0.9f));
    }

    public const int Rows = 19;
    const int ListX = 24, ListW = 560, RowH = 30, ListY = 84;

    void DrawList(MenuModel m, double t)
    {
        int count = m.Rows.Count;
        int top = Math.Clamp(m.Index - Rows / 2, 0, Math.Max(0, count - Rows));
        // Smooth scrolling
        m.ScrollPos += (top - m.ScrollPos) * 0.25;
        if (Math.Abs(top - m.ScrollPos) < 0.01) m.ScrollPos = top;
        bool grouped = m.GroupBy != "";
        for (int row = -1; row <= Rows; row++)
        {
            int i = (int)Math.Floor(m.ScrollPos) + row;
            if (i < 0 || i >= count) continue;
            int y = ListY + (int)Math.Round((i - m.ScrollPos) * RowH);
            if (y < ListY - 4 || y > ListY + Rows * RowH - RowH + 4) continue;
            bool sel = i == m.Index;
            var r = m.Rows[i];
            var g = r.Game;
            int tx = ListX + (r.IsHeader ? 34 : grouped && g != null ? 40 : 14);
            int tw = ListX + ListW - 10 - tx;
            string name = g != null ? GameTags.ShortTitle(g.Title) : r.IsHeader ? r.Group! : "Exit arcade";
            string? count2 = r.IsHeader ? r.Count.ToString() : null;
            if (count2 != null) tw -= _f.Measure(count2, 2) + 16;
            uint col = r.IsHeader ? Cyan : g == null ? Grey : g.Runnable ? White : Canvas.Rgb(170, 80, 80);
            bool open = r.IsHeader && m.Open.Contains(r.Group!);

            if (r.IsHeader && !sel) C.Fill(ListX, y - 3, ListW, RowH - 2, Canvas.Rgb(16, 26, 70), 0.85f);
            if (sel)
            {
                float pulse = (float)(0.5 + 0.5 * Math.Sin(t * 5));
                uint bar = g == null && !r.IsHeader ? Canvas.Rgb(120, 120, 130)
                    : g == null || g.Runnable ? Canvas.Mix(Canvas.Rgb(0, 60, 170), Canvas.Rgb(0, 100, 220), pulse)
                    : Canvas.Mix(Canvas.Rgb(110, 15, 15), Canvas.Rgb(170, 30, 30), pulse);
                C.Fill(ListX, y - 3, ListW, RowH - 2, bar);
                C.Fill(ListX, y - 3, 5, RowH - 2, Yellow);
                col = r.IsHeader ? Yellow : White;
            }
            if (r.IsHeader) Triangle(ListX + 14, y + 5, open, Yellow);
            if (sel) Marquee(name, tx, y, tw, 2, col, t, Shadow);
            else _f.Draw(C, Fit(name, tw, 2), tx, y, 2, col, 1, Shadow);
            if (count2 != null) _f.Draw(C, count2, ListX + ListW - 12 - _f.Measure(count2, 2), y, 2, sel ? White : Grey);
        }
        if (m.All.Count == 0) _f.Draw(C, "No games found", ListX + 14, ListY + 2 * RowH, 2, Red, 1, Shadow);
        // Scroll hints
        if (top > 0) Arrow(ListX + ListW / 2, ListY - 12, true, t);
        if (top + Rows < count) Arrow(ListX + ListW / 2, ListY + Rows * RowH - 2, false, t);
    }

    /// <summary>A header's open (pointing down) / closed (pointing right) marker.</summary>
    void Triangle(int x, int y, bool open, uint c)
    {
        for (int i = 0; i < 7; i++)
        {
            if (open) C.Fill(x + i, y + 3 + i, 13 - 2 * i, 1, c);
            else C.Fill(x + 3 + i, y + i, 1, 13 - 2 * i, c);
        }
    }

    void Arrow(int cx, int y, bool up, double t)
    {
        if (((int)(t * 2) & 1) == 1) return;
        for (int i = 0; i < 6; i++) C.Fill(cx - i, up ? y + i : y + 5 - i, i * 2 + 1, 1, Yellow);
    }

    string Fit(string s, int width, int scale)
    {
        if (_f.Measure(s, scale) <= width) return s;
        while (s.Length > 1 && _f.Measure(s + "..", scale) > width) s = s[..^1];
        return s.TrimEnd() + "..";
    }

    /// <summary>Text that scrolls sideways when it doesn't fit (clipped to its box).</summary>
    void Marquee(string s, int x, int y, int width, int scale, uint color, double t, uint? shadow)
    {
        int tw = _f.Measure(s, scale);
        if (tw <= width) { _f.Draw(C, s, x, y, scale, color, 1, shadow); return; }
        int gap = 60, span = tw + gap;
        double pause = 1.2, speed = 60;
        double cycle = pause + span / speed;
        double ph = t % cycle;
        int off = ph < pause ? 0 : (int)((ph - pause) * speed);
        // Draw into a scratch canvas row band and copy the visible window
        var tmp = new Canvas(width, _f.Height * scale + scale);
        for (int yy = 0; yy < tmp.H; yy++)
            for (int xx = 0; xx < width; xx++)
                if (y + yy < H && x + xx < W) tmp.Pix[yy * width + xx] = C.Pix[(y + yy) * W + x + xx];
        if (shadow is uint sh) { _f.Draw(tmp, s, -off + scale, scale, scale, sh); _f.Draw(tmp, s, -off + span + scale, scale, scale, sh); }
        _f.Draw(tmp, s, -off, 0, scale, color);
        _f.Draw(tmp, s, -off + span, 0, scale, color);
        for (int yy = 0; yy < tmp.H; yy++)
            for (int xx = 0; xx < width; xx++)
                if (y + yy < H && x + xx < W) C.Pix[(y + yy) * W + x + xx] = tmp.Pix[yy * width + xx];
    }

    const int PanX = 616, PanW = 640, PanY = 84, ShotH = 400;

    void Monitor()
    {
        C.Fill(PanX - 5, PanY - 5, PanW + 10, ShotH + 10, Canvas.Rgb(24, 24, 32));
        C.Frame(PanX - 5, PanY - 5, PanW + 10, ShotH + 10, Canvas.Rgb(70, 70, 90), 2);
        C.Fill(PanX, PanY, PanW, ShotH, Black);
    }

    void DrawPanel(MenuModel m, GameInfo g, double t)
    {
        Monitor();
        if (!g.Runnable) DrawMissing(g, t);
        else
        {
            var snaps = Snaps(g.Set);
            if (snaps.Count == 0) DrawStatic(t, m.SnapJobOn == g.Set ? "Warming up..." : "No picture yet");
            else
            {
                double per = 3.2, fade = 0.5;
                double local = t - m.SelectedAt;
                int i = (int)(local / per) % snaps.Count;
                double ph = local % per;
                DrawShot(snaps[i], 1);
                if (snaps.Count > 1 && ph > per - fade) DrawShot(snaps[(i + 1) % snaps.Count], (float)((ph - (per - fade)) / fade));
            }
            // Brief "channel change" static when the selection changes
            double since = t - m.SelectedAt;
            if (since < 0.18) StaticNoise(PanX, PanY, PanW, ShotH, (float)(1 - since / 0.18));
        }

        double ts = t - m.SelectedAt;
        int y = PanY + ShotH + 14;
        Marquee(g.Title, PanX, y, PanW, 3, Yellow, ts, Shadow);
        y += 44;
        string line2 = $"{g.Year}   {g.Maker}";
        if (g.Version.Length > 0) line2 += $"   ({g.Version})";
        Marquee(line2, PanX, y, PanW, 2, White, ts, null);
        y += 30;
        string line3 = (g.Players <= 0 ? "" : g.Players == 1 ? "1 player   " : $"{g.Players} players   ") + g.ControlText();
        Marquee(line3, PanX, y, PanW, 2, Grey, ts, null);
        y += 30;
        if (g.Genre != "OTHER") { _f.Draw(C, GameTags.GenreName(g.Genre), PanX, y, 2, Grey); y += 30; }
        string? hint = g.Runnable ? g.PadHint() : null;
        if (g.Runnable && g.Emulation is "imperfect" or "preliminary") hint = (hint != null ? hint + "   " : "") + "MAME: " + g.Emulation;
        if (g.Runnable && g.Problem == "checksums") hint = (hint != null ? hint + "   " : "") + "ROMs differ from MAME's";
        if (hint != null) Marquee(hint, PanX, y, PanW, 2, Cyan, ts, null);
    }

    /// <summary>A group header: the monitor flicks through the group's games, with the group's name and size below.</summary>
    void DrawGroupPanel(MenuModel m, MenuRow row, double t)
    {
        Monitor();
        var games = m.Groups.First(gr => gr.Name == row.Group).Games.Where(g => g.Runnable).ToList();
        double per = 2.4, local = t - m.SelectedAt;
        var shown = games.Count == 0 ? null : games[(int)(local / per) % games.Count];
        var snaps = shown == null ? null : Snaps(shown.Set);
        if (snaps is { Count: > 0 })
        {
            DrawShot(snaps[0], 1);
            string cap = GameTags.ShortTitle(shown!.Title);
            int cw = Math.Min(PanW, _f.Measure(cap, 2) + 24);
            C.Fill(PanX + (PanW - cw) / 2, PanY + ShotH - 34, cw, 30, Black, 0.75f);
            _f.DrawCentered(C, Fit(cap, PanW - 24, 2), PanX + PanW / 2, PanY + ShotH - 30, 2, White);
        }
        else DrawStatic(t, "No pictures yet");
        double phase = local % per;
        if (local > 0.18 && phase < 0.12) StaticNoise(PanX, PanY, PanW, ShotH, (float)(1 - phase / 0.12) * 0.7f);
        else if (local < 0.18) StaticNoise(PanX, PanY, PanW, ShotH, (float)(1 - local / 0.18));

        bool open = m.Open.Contains(row.Group!);
        int y = PanY + ShotH + 14;
        Marquee(row.Group!, PanX, y, PanW, 3, Yellow, local, Shadow);
        y += 44;
        _f.Draw(C, row.Count == 1 ? "1 game" : $"{row.Count} games", PanX, y, 2, White);
        y += 30;
        _f.Draw(C, open ? $"{Label("Menu", "Start")}: hide the games     {Label("Menu", "Back")}: close" : $"{Label("Menu", "Start")}: show the games", PanX, y, 2, Cyan);
        y += 30;
        _f.Draw(C, "Left / Right: previous / next group", PanX, y, 2, Grey);
    }

    void DrawShot(Img img, float alpha)
    {
        // Arcade monitors were 4:3 (3:4 when mounted vertically): show it that way, whatever the pixel size.
        bool vertical = img.H > img.W;
        int h = ShotH, w = vertical ? h * 3 / 4 : h * 4 / 3;
        if (w > PanW) { w = PanW; h = vertical ? w * 4 / 3 : w * 3 / 4; }
        C.Draw(img, PanX + (PanW - w) / 2, PanY + (ShotH - h) / 2, w, h, alpha);
    }

    void StaticNoise(int x, int y, int w, int h, float a)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int v = _rng.Next(256);
                C.Plot(xx, yy, Canvas.Rgb(v, v, v), a * 0.85f);
            }
    }

    void DrawStatic(double t, string msg)
    {
        StaticNoise(PanX, PanY, PanW, ShotH, 0.55f);
        // Rolling dark band, like a detuned set
        int band = (int)(t * 120 % (ShotH + 80)) - 40;
        int b0 = Math.Max(0, band), b1 = Math.Min(ShotH, band + 40);
        if (b1 > b0) C.Fill(PanX, PanY + b0, PanW, b1 - b0, Black, 0.5f);
        int bw = _f.Measure(msg, 2) + 28;
        C.Fill(PanX + (PanW - bw) / 2, PanY + ShotH / 2 - 18, bw, 36, Black, 0.85f);
        _f.DrawCentered(C, msg, PanX + PanW / 2, PanY + ShotH / 2 - 11, 2, White);
    }

    void DrawMissing(GameInfo g, double t)
    {
        StaticNoise(PanX, PanY, PanW, ShotH, 0.25f);
        C.Fill(PanX + 12, PanY + 12, PanW - 24, ShotH - 24, Black, 0.85f);
        _f.DrawCentered(C, "Can't start", PanX + PanW / 2, PanY + 26, 3, Red, 1, Shadow);
        _f.DrawCentered(C, "ROM files missing:", PanX + PanW / 2, PanY + 70, 2, White);
        int y = PanY + 108;
        var items = g.Missing.Take(9).ToList();
        foreach (var item in items) { _f.Draw(C, Fit(item, PanW - 48, 2), PanX + 24, y, 2, Grey); y += 28; }
        if (g.Missing.Count > items.Count) _f.Draw(C, $"+ {g.Missing.Count - items.Count} more", PanX + 24, y, 2, Grey);
    }

    void DrawExitPanel(double t)
    {
        Monitor();
        _f.DrawCentered(C, "GAME OVER", PanX + PanW / 2, PanY + ShotH / 2 - 40, 4, Red, 1, Shadow);
        if (((int)(t * 2) & 1) == 0) _f.DrawCentered(C, $"Press {Label("Menu", "Start")} to exit", PanX + PanW / 2, PanY + ShotH / 2 + 24, 2, White);
    }

    void DrawFooter(MenuModel m, double t)
    {
        const int fy = 668;
        C.Fill(0, fy, W, H - fy, Black, 0.6f);
        for (int px = 0; px < W; px++) C.Plot(px, fy, Canvas.Rgb(50, 50, 70));
        // button names come from config\input.json (whichever kind of controller is plugged in)
        string L(string section, string name) => Label(section, name);
        string start = L("Menu", "Start"), back = L("Menu", "Back"), filter = L("Menu", "Filter"), option = L("Menu", "Option");
        string inGame = $"In game: {L("Mame", "COIN1")} = coin    {L("Mame", "START1")} = start";
        string quit = $"Hold {L("Hotkeys", "ExitGame")} to quit a game";
        string[] tips = m.GroupBy == "" ?
            new[]
            {
                $"{start}: play",
                "Left / Right: page up / down",
                $"{filter}: sort into groups by genre, maker, decade, players or controls",
                inGame,
                quit,
                $"{option}: game CRT = {CrtName(m.GameCrt)}",
            } :
            new[]
            {
                $"{start}: open a group, or play a game",
                $"{back}: close the group",
                "Left / Right: previous / next group",
                $"{filter}: group by genre, maker, decade, players, controls, or A-Z",
                inGame,
                quit,
                $"{option}: game CRT = {CrtName(m.GameCrt)}",
            };
        int tip = (int)(t / 3.5) % tips.Length;
        _f.Draw(C, tips[tip], 24, fy + 16, 2, Grey);
        if (((int)(t * 1.6) & 1) == 0) _f.Draw(C, "FREE PLAY", W - 24 - _f.Measure("FREE PLAY", 2), fy + 16, 2, Yellow);
    }

    /// <summary>Friendly name of an action's button, e.g. ("Menu", "Start") -> "A". Set by MainForm from input.json.</summary>
    public static Func<string, string, string> Label = (_, name) => name;

    public static string CrtName(string s) => s switch { "geom" => "CRT-Geom", "off" => "off", _ => "CRT-Guest" }; // "hlsl" setting: Guest on raster games, HLSL on vector

    void DrawToast(string msg)
    {
        int w = _f.Measure(msg, 2) + 40;
        int x = (W - w) / 2, y = H / 2 - 26;
        C.Fill(x, y, w, 52, Canvas.Rgb(0, 0, 0), 0.9f);
        C.Frame(x, y, w, 52, Yellow, 2);
        _f.DrawCentered(C, msg, W / 2, y + 15, 2, White);
    }

    void DrawConfirm(double t)
    {
        C.Fill(0, 0, W, H, Black, 0.6f);
        int w = 460, h = 150, x = (W - w) / 2, y = (H - h) / 2;
        C.Fill(x, y, w, h, Canvas.Rgb(10, 10, 30));
        C.Frame(x, y, w, h, Red, 3);
        _f.DrawCentered(C, "Exit arcade?", W / 2, y + 30, 3, White, 1, Shadow);
        _f.DrawCentered(C, $"{Label("Menu", "Start")}: yes      {Label("Menu", "Back")}: no", W / 2, y + 96, 2, Yellow);
    }
}

/// <summary>One line of the menu list: a group header, a game, or the exit line at the end.</summary>
class MenuRow
{
    public GameInfo? Game;
    public string? Group;   // the group this line heads (header) or sits in (game, when grouped)
    public int Count;       // header: games in the group
    public bool IsHeader => Game == null && Group != null;
}

/// <summary>What the menu is showing.</summary>
class MenuModel
{
    public List<GameInfo> All = new();      // every game, sorted
    public string GroupBy = "";             // "" = plain A-Z list, else see GameTags.GroupModes
    public List<(string Name, List<GameInfo> Games)> Groups = new();
    public readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase); // expanded groups
    public List<MenuRow> Rows = new() { new MenuRow() };
    public int Index;
    public double ScrollPos;
    public double SelectedAt;
    public string? Toast;
    public double ToastUntil;
    public bool ConfirmExit;
    public string GameCrt = "hlsl";
    public string? SnapJobOn;
    public MenuRow Row => Rows[Math.Clamp(Index, 0, Rows.Count - 1)];
    public GameInfo? Selected => Row.Game;

    /// <summary>Sorts the games into groups again (all closed).</summary>
    public void Regroup()
    {
        Groups = GroupBy == "" ? new() : GameTags.Group(All, GroupBy);
        Open.Clear();
        Rebuild();
    }

    /// <summary>Rebuilds the visible lines from the groups and which ones are open.</summary>
    public void Rebuild()
    {
        Rows = new();
        if (GroupBy == "") foreach (var g in All) Rows.Add(new MenuRow { Game = g });
        else
            foreach (var (name, games) in Groups)
            {
                Rows.Add(new MenuRow { Group = name, Count = games.Count });
                if (Open.Contains(name)) foreach (var g in games) Rows.Add(new MenuRow { Game = g, Group = name });
            }
        Rows.Add(new MenuRow()); // exit
        Index = Math.Clamp(Index, 0, Rows.Count - 1);
    }

    int HeaderIndex(string name) => Rows.FindIndex(r => r.IsHeader && r.Group == name);

    /// <summary>Puts the cursor on a game: in a group list, on its group's header (or on the game itself, opening the group).</summary>
    public void Reveal(string set, bool openGroup)
    {
        if (GroupBy == "") { Index = Math.Max(0, Rows.FindIndex(r => r.Game?.Set == set)); return; }
        var grp = Groups.FirstOrDefault(gr => gr.Games.Any(g => g.Set == set)).Name;
        if (grp == null) { Index = 0; return; }
        if (openGroup)
        {
            Open.Add(grp);
            Rebuild();
            Index = Rows.FindIndex(r => r.Group == grp && r.Game?.Set == set);
        }
        else Index = HeaderIndex(grp);
    }

    /// <summary>Opens or closes the group under the cursor (header), or closes the one the selected game is in.</summary>
    public void Toggle(bool closeOnly = false)
    {
        var name = Row.Group;
        if (name == null) return;
        if (!Open.Remove(name) && !closeOnly) Open.Add(name);
        Rebuild();
        Index = HeaderIndex(name);
    }

    /// <summary>Previous / next group header (the exit line counts as the last stop).</summary>
    public int StepGroup(int dir)
    {
        var stops = Rows.Select((r, i) => (r, i)).Where(x => x.r.IsHeader || x.i == Rows.Count - 1).Select(x => x.i).ToList();
        if (dir > 0) return stops.FirstOrDefault(i => i > Index, stops[0]);
        return stops.LastOrDefault(i => i < Index, stops[^1]);
    }
}
