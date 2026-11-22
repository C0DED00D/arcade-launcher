namespace ArcadeLauncher;

/// <summary>Command-line options (mostly for testing).</summary>
class Options
{
    public bool Windowed;          // --windowed: 1280x720 window instead of full screen
    public string? Run;            // --run <set>: start a game straight away
    public string? Select;         // --select <set>: start with this game highlighted
    public string? ShotPath;       // --shot <file.png>: save what's on screen ...
    public double ShotAfter = 6;   // --shot-after <seconds>: ... this long after start
    public bool QuitAfterShot;     // --quit-after-shot
    public List<Act> Acts = new(); // --acts filter,right,down,...: menu presses to make on entering the menu
    public bool InputTest;         // --input-test: show the binding name of every control pressed
    public bool WriteCtrlr;        // --write-ctrlr: write config\input.json (if missing) and MAME's ctrlr\launcher.cfg, then quit

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string arg = a[i].ToLowerInvariant();
            string? next = i + 1 < a.Length ? a[i + 1] : null;
            switch (arg)
            {
                case "--windowed": o.Windowed = true; break;
                case "--run": o.Run = next; i++; break;
                case "--select": o.Select = next; i++; break;
                case "--shot": o.ShotPath = next; i++; break;
                case "--shot-after": o.ShotAfter = double.Parse(next!, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--quit-after-shot": o.QuitAfterShot = true; break;
                case "--input-test": o.InputTest = true; break;
                case "--write-ctrlr": o.WriteCtrlr = true; break;
                case "--acts":
                    foreach (var s in (next ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (Enum.TryParse<Act>(s, true, out var act)) o.Acts.Add(act);
                    i++; break;
            }
        }
        return o;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt.InputTest) { InputTest.Run(); return; }
        using var single = new Mutex(true, "ArcadeLauncher.Single", out bool first);
        if (!first) return;

        var paths = new Paths(FindRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogFile)!);
        Log.Init(paths.LogFile);
        Log.Write("---- start, root " + paths.Root);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("CRASH: " + e.ExceptionObject);

        if (!File.Exists(paths.MameExe))
        {
            MessageBox.Show("MAME not found at\n" + paths.MameExe, "Arcade");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Settings)!);
        var settings = Settings.Load(paths.Settings);
        var input = InputConfig.Load(paths.InputConfig, settings); // (takes over settings.json "Keys" from older versions)
        settings.Save(paths.Settings); // writes any new options with their defaults, so they can be edited
        if (opt.WriteCtrlr) { MameCtrlr.Write(input, paths.MameDir); return; }
        try
        {
            Application.Run(new MainForm(paths, settings, input, opt));
        }
        catch (Exception ex)
        {
            Log.Write("fatal: " + ex);
            Cursor.Show();
            MessageBox.Show(ex.Message, "Arcade");
        }
    }

    /// <summary>The arcade root is the folder that holds emulators\mame (the exe lives in app\).</summary>
    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "emulators", "mame", "mame.exe"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
