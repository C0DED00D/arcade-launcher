using System.Diagnostics;
using System.Drawing.Imaging;

namespace ArcadeLauncher;

enum Mode { Boot, Menu, Launching, InGame, Returning, Exiting }

/// <summary>The full-screen window: runs the frame loop, the menu logic and the hand-over to MAME.</summary>
sealed class MainForm : Form
{
    readonly Paths _paths;
    readonly Settings _settings;
    readonly Options _opt;
    readonly Library _lib;
    readonly SnapshotJob _snaps;
    readonly MenuScreen _screen;
    readonly MenuModel _model = new();
    readonly MenuInput _input;
    readonly InputConfig _inputCfg;
    readonly Sounds _sounds = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    CrtRenderer? _crt;
    Mode _mode = Mode.Boot;
    double _last, _power, _modeAt;
    GameRunner? _runner;
    readonly List<(string, string)> _bootLines = new();
    volatile bool _bootDone;
    Exception? _bootError;
    bool _shotTaken;

    public MainForm(Paths paths, Settings settings, InputConfig input, Options opt)
    {
        _paths = paths; _settings = settings; _opt = opt; _inputCfg = input;
        _lib = new Library(paths);
        _snaps = new SnapshotJob(paths, _lib);
        _snaps.SnapsReady += set => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => _screen!.ForgetSnaps(set)); };
        _screen = new MenuScreen(paths);
        _input = new MenuInput(input);
        MenuScreen.Label = input.Label;
        _sounds.Enabled = settings.Sounds;
        _model.GameCrt = settings.GameCrt;

        Text = "Arcade";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = opt.Windowed ? new Rectangle(screen.X + 80, screen.Y + 80, 1280, 720) : screen;
        if (opt.Windowed) FormBorderStyle = FormBorderStyle.Sizable;
        Icon = SystemIcons.Application;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { _crt = new CrtRenderer(Handle, MenuScreen.W, MenuScreen.H); }
        catch (Exception ex)
        {
            Log.Write("renderer failed: " + ex);
            Cursor.Show();
            MessageBox.Show("The CRT renderer couldn't start (needs OpenGL 3.3):\n\n" + ex.Message, "Arcade");
            Environment.Exit(1);
        }
        Log.Write("renderer: " + _crt.Info);
        Log.Write($"xinput: {(XInput.Available ? "yes" : "no")}, guide button: {(XInput.HasGuide ? "yes" : "no")}");
        InputDevices.Poll();
        Log.Write($"input: XInput pads [{string.Join(", ", InputDevices.Pads())}], joysticks [{string.Join("; ", InputDevices.Joysticks().Select(j => $"{j.Number} = {j.Id}"))}]");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_opt.Windowed) Cursor.Hide();
        new Thread(BootScan) { IsBackground = true, Name = "boot" }.Start();
        Application.Idle += (_, _) => { while (AppIdle()) SafeStep(); };
    }

    string? _lastError;
    void SafeStep()
    {
        try { Step(); }
        catch (Exception ex)
        {
            // Log each distinct error once and keep going: a drawing bug mustn't drop the player to the desktop.
            if (ex.Message != _lastError) { _lastError = ex.Message; Log.Write("frame error: " + ex); }
            Thread.Sleep(10);
        }
    }

    static bool AppIdle() => !Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // suppressing every key stops Windows seeing Alt+F4, so close here
        if (e.Alt && e.KeyCode == Keys.F4) { e.Handled = true; Close(); return; }
        _input.KeyDown(e.KeyCode); e.Handled = true; e.SuppressKeyPress = true;
    }
    protected override void OnKeyUp(KeyEventArgs e) { _input.KeyUp(e.KeyCode); e.Handled = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); _input.ClearKeys(); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.F4)) { Log.Write("Alt+F4: closing"); Close(); return true; } // the menu's key handling would otherwise swallow it
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override bool ProcessDialogKey(Keys keyData) => true; // arrows / Tab / Enter belong to the menu
    protected override bool IsInputKey(Keys keyData) => true;        // ...so they reach OnKeyDown instead of being eaten

    void BootScan()
    {
        try
        {
            _lib.Scan((name, res) => { lock (_bootLines) _bootLines.Add((name, res)); });
        }
        catch (Exception ex) { _bootError = ex; Log.Write("boot scan failed: " + ex); lock (_bootLines) _bootLines.Add(("MAME", "BAD")); }
        _bootDone = true;
    }

    void SetMode(Mode m) { _mode = m; _modeAt = _clock.Elapsed.TotalSeconds; }

    void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _last);
        _last = now;
        double inMode = now - _modeAt;

        if (_mode == Mode.InGame) { GameTick(dt); return; }

        _input.Update(now, dt);
        switch (_mode)
        {
            case Mode.Boot:
                _power = Math.Min(1, inMode / 0.9);
                lock (_bootLines) _screen.DrawBoot(_bootLines, now, _bootDone);
                if (_bootDone && _bootError == null && inMode > 1.0 && (_bootDoneAt ??= now) + 1.2 < now) EnterMenu(now);
                if (_bootError != null && _input.Next(out _)) Close();
                break;
            case Mode.Menu:
                MenuTick(now);
                _screen.DrawMenu(_model, now);
                break;
            case Mode.Launching:
                _power = Math.Max(0, 1 - inMode / 0.45);
                _screen.DrawMenu(_model, now);
                if (_power <= 0) StartGame();
                break;
            case Mode.Returning:
                _power = Math.Min(1, inMode / 0.8);
                _screen.DrawMenu(_model, now);
                if (_power >= 1) SetMode(Mode.Menu);
                while (_input.Next(out _)) { }
                break;
            case Mode.Exiting:
                _power = Math.Max(0, 1 - inMode / 0.6);
                _screen.DrawMenu(_model, now);
                if (_power <= 0) { Close(); return; }
                break;
        }
        _crt!.Render(_screen.C.Pix, ClientSize.Width, ClientSize.Height, (float)now, (float)_power, _settings.Crt, _settings.MenuCrt);
        if (_opt.ShotPath != null && !_shotTaken && now > _opt.ShotAfter) TakeShot();
    }

    double? _bootDoneAt;

    void EnterMenu(double now)
    {
        // light gun games are left out unless the cabinet has a gun (settings.json HideLightGunGames)
        bool hideGuns = _settings.HideLightGunGames;
        _model.All = _lib.Games.Where(g => !g.Hidden && !(hideGuns && (g.Controls.Contains("lightgun") || g.Genre == "LIGHT GUN")))
            .OrderBy(g => g.Runnable ? 0 : 1).ThenBy(g => GameTags.ShortTitle(g.Title), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
        _model.GroupBy = ""; // always starts as the A-Z list; X sorts into groups for this run
        _model.Regroup();
        Log.Write($"groups by {GameTags.GroupModeName(_model.GroupBy)}: {string.Join(", ", _model.Groups.Select(g => $"{g.Name} ({g.Games.Count})"))}");
        // A game asked for on the command line is shown with its group open; the last game played just has its group picked.
        string? asked = _opt.Select ?? _opt.Run;
        string? want = asked ?? _settings.LastGame;
        if (!string.IsNullOrEmpty(want)) _model.Reveal(want, openGroup: asked != null);
        _model.ScrollPos = Math.Clamp(_model.Index - MenuScreen.Rows / 2, 0, Math.Max(0, _model.Rows.Count - MenuScreen.Rows));
        _model.SelectedAt = now;
        SetMode(Mode.Menu);
        _snaps.Start(_model.All);
        foreach (var a in _opt.Acts) _input.Inject(a);
        if (_opt.Run != null && _model.Selected?.Set == _opt.Run) Launch(now);
    }

    /// <summary>X: the next way of sorting the list, keeping the cursor on the same game's group.</summary>
    void NextGrouping(double now)
    {
        string? keep = _model.Selected?.Set ?? (_model.Row.IsHeader ? _model.Groups.First(g => g.Name == _model.Row.Group).Games[0].Set : null);
        int i = Array.IndexOf(GameTags.GroupModes, _model.GroupBy);
        _model.GroupBy = GameTags.GroupModes[(i + 1) % GameTags.GroupModes.Length];
        _model.Regroup();
        _model.Index = 0;
        if (keep != null) _model.Reveal(keep, openGroup: false);
        _model.SelectedAt = now;
        Toast(_model.GroupBy == "" ? "A-Z list" : "Grouped by " + GameTags.GroupModeName(_model.GroupBy), now);
        _sounds.Toggle();
    }

    void MenuTick(double now)
    {
        _model.SnapJobOn = _snaps.Current;
        while (_input.Next(out var a))
        {
            int count = _model.Rows.Count;
            if (_model.ConfirmExit)
            {
                if (a == Act.Start) { _sounds.Back(); SetMode(Mode.Exiting); }
                else if (a is Act.Back or Act.Exit) { _model.ConfirmExit = false; _sounds.Back(); }
                continue;
            }
            const int page = MenuScreen.Rows - 1;
            bool grouped = _model.GroupBy != "";
            switch (a)
            {
                case Act.Up: Select((_model.Index - 1 + count) % count, now); break;
                case Act.Down: Select((_model.Index + 1) % count, now); break;
                // grouped: jump between headers. A-Z: a page at a time; at the end of the list, one more press wraps round
                case Act.Left when grouped: Select(_model.StepGroup(-1), now); break;
                case Act.Right when grouped: Select(_model.StepGroup(1), now); break;
                case Act.Left: Select(_model.Index == 0 ? count - 1 : Math.Max(0, _model.Index - page), now); break;
                case Act.Right: Select(_model.Index == count - 1 ? 0 : Math.Min(count - 1, _model.Index + page), now); break;
                case Act.Filter: NextGrouping(now); break;
                case Act.Start:
                    if (_model.Row.IsHeader) { _model.Toggle(); _sounds.Toggle(); }
                    else if (_model.Selected == null) { _model.ConfirmExit = true; _sounds.Toggle(); }
                    else Launch(now);
                    break;
                case Act.Back when grouped && _model.Row.Group != null && (!_model.Row.IsHeader || _model.Open.Contains(_model.Row.Group)):
                    _model.Toggle(closeOnly: true);
                    _model.SelectedAt = now;
                    _sounds.Back();
                    break;
                case Act.Option:
                    _settings.GameCrt = _settings.GameCrt switch { "hlsl" => "geom", "geom" => "off", _ => "hlsl" };
                    _model.GameCrt = _settings.GameCrt;
                    _settings.Save(_paths.Settings);
                    Toast("Game CRT: " + MenuScreen.CrtName(_settings.GameCrt), now);
                    _sounds.Toggle();
                    break;
                case Act.Exit: _model.ConfirmExit = true; _sounds.Toggle(); break;
            }
        }
        // Attract: after a minute untouched, wander through the games on its own.
        // (only through the games on show: in a group list with every group closed, it stays put)
        if (_input.IdleSeconds > 60 && now - _model.SelectedAt > 9.6)
        {
            int n = _model.Rows.Count;
            for (int k = 1; k < n; k++)
            {
                int j = (_model.Index + k) % n;
                if (_model.Rows[j].Game?.Runnable == true) { _model.Index = j; _model.SelectedAt = now; break; }
            }
        }
    }

    void Select(int i, double now)
    {
        if (i == _model.Index) return;
        _model.Index = i; _model.SelectedAt = now;
        _sounds.Move();
    }

    void Toast(string msg, double now) { _model.Toast = msg; _model.ToastUntil = now + 1.8; }

    void Launch(double now)
    {
        var g = _model.Selected!;
        if (!g.Runnable) { _sounds.Buzz(); Toast("ROM files missing", now); return; }
        _settings.LastGame = g.Set;
        _settings.Save(_paths.Settings);
        _sounds.Coin();
        SetMode(Mode.Launching);
    }

    void StartGame()
    {
        var g = _model.Selected!;
        _snaps.Stop();
        // Leave a black screen behind MAME so the desktop never shows during the switch.
        _crt!.Render(_screen.C.Pix, ClientSize.Width, ClientSize.Height, 0, 0, _settings.Crt, _settings.MenuCrt);
        try
        {
            _runner = new GameRunner(_paths, g, _settings, _inputCfg);
            SetMode(Mode.InGame);
        }
        catch (Exception ex)
        {
            Log.Write("couldn't start MAME: " + ex);
            Toast("MAME wouldn't start", _clock.Elapsed.TotalSeconds);
            SetMode(Mode.Returning);
        }
    }

    void GameTick(double dt)
    {
        var r = _runner!;
        r.Tick(dt, _settings.ExitHoldSeconds);
        if (r.Exited)
        {
            Log.Write($"{r.Game.System switch { "arcade" => "MAME", "jaguar" => "BigPEmu", "saturn" => "Mednafen", _ => "Flycast" }} ended: exit code {r.ExitCode} after {r.RunTime.TotalSeconds:0}s");
            double now = _clock.Elapsed.TotalSeconds;
            if (r.ExitCode != 0 && r.RunTime.TotalSeconds < 8)
            {
                Log.Write("MAME said: " + r.ErrorText);
                var line = r.ErrorText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                Toast(line.Contains("missing", StringComparison.OrdinalIgnoreCase) ? "ROM files missing" : $"{(r.Game.System == "arcade" ? "MAME" : "Emulator")} error {r.ExitCode}", now);
            }
            _runner = null;
            Win32.ForceForeground(Handle);
            Activate();
            _input.ClearKeys();
            _input.Flush();
            _model.SelectedAt = now;
            SetMode(Mode.Returning);
            _snaps.Start(_model.All);
            return;
        }
        Thread.Sleep(15);
    }

    unsafe void TakeShot()
    {
        _shotTaken = true;
        int w = ClientSize.Width, h = ClientSize.Height;
        var px = new byte[w * h * 4];
        fixed (byte* p = px) GL.glReadPixels(0, 0, w, h, GL.BGRA, GL.UNSIGNED_BYTE, p);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(px, (h - 1 - y) * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        bmp.Save(_opt.ShotPath!, ImageFormat.Png);
        Log.Write("screenshot saved: " + _opt.ShotPath);
        if (_opt.QuitAfterShot) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _runner?.Kill();
        _snaps.Stop();
        _settings.Save(_paths.Settings);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _crt?.Dispose();
        Cursor.Show();
        base.OnFormClosed(e);
    }
}
