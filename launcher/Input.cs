namespace ArcadeLauncher;

enum Act { Up, Down, Left, Right, Start, Back, Option, Filter, Exit }

/// <summary>
/// Menu input: every device bound in config\input.json "Menu" (keyboard, XInput pads, DirectInput controllers, mouse
/// buttons). Gives edge-triggered actions with auto-repeat on the directions.
/// </summary>
class MenuInput
{
    const int N = (int)Act.Exit + 1;
    readonly InputConfig _cfg;
    readonly HashSet<Keys> _keysDown = new(), _keysTapped = new();
    readonly bool[] _held = new bool[N], _prev = new bool[N];
    readonly double[] _repeatAt = new double[N];
    readonly Queue<Act> _pressed = new();
    public double IdleSeconds { get; private set; }

    public MenuInput(InputConfig cfg) { _cfg = cfg; }

    // Keyboard keys come from the window (so they only count while the launcher has the focus). A tap shorter than a
    // frame still counts: it's remembered until the next Update.
    public void KeyDown(Keys k) { if (_keysDown.Add(k)) _keysTapped.Add(k); }
    public void KeyUp(Keys k) => _keysDown.Remove(k);
    public void ClearKeys() { _keysDown.Clear(); _keysTapped.Clear(); }
    bool Key(Keys k) => _keysDown.Contains(k) || _keysTapped.Contains(k) || Generic(k);
    // the window reports Ctrl/Shift/Alt as ControlKey/ShiftKey/Menu: a binding to the left/right one matches those too
    bool Generic(Keys k) => k switch
    {
        Keys.LControlKey or Keys.RControlKey => _keysDown.Contains(Keys.ControlKey) && (Win32.GetAsyncKeyState((int)k) & 0x8000) != 0,
        Keys.LShiftKey or Keys.RShiftKey => _keysDown.Contains(Keys.ShiftKey) && (Win32.GetAsyncKeyState((int)k) & 0x8000) != 0,
        Keys.LMenu or Keys.RMenu => _keysDown.Contains(Keys.Menu) && (Win32.GetAsyncKeyState((int)k) & 0x8000) != 0,
        _ => false,
    };

    public void Update(double now, double dt)
    {
        InputDevices.Poll();
        Array.Clear(_held);
        foreach (var (act, b) in _cfg.MenuBindings) _held[(int)act] = b.IsDown(Key);
        _keysTapped.Clear();

        bool anything = false;
        for (int i = 0; i < N; i++)
        {
            if (_held[i]) anything = true;
            bool dir = i <= (int)Act.Right;
            if (_held[i] && !_prev[i]) { _pressed.Enqueue((Act)i); _repeatAt[i] = now + 0.38; }
            else if (_held[i] && dir && now >= _repeatAt[i]) { _pressed.Enqueue((Act)i); _repeatAt[i] = now + 0.075; }
            _prev[i] = _held[i];
        }
        IdleSeconds = anything || _pressed.Count > 0 ? 0 : IdleSeconds + dt;
    }

    public bool Next(out Act a) => _pressed.TryDequeue(out a);
    /// <summary>Queues a press as if it came from a controller (testing: --acts).</summary>
    public void Inject(Act a) => _pressed.Enqueue(a);
    /// <summary>Forget queued presses and ignore anything still held until it's released (e.g. the exit combo).</summary>
    public void Flush() { _pressed.Clear(); Array.Fill(_prev, true); Array.Fill(_repeatAt, double.MaxValue); }
}

/// <summary>Input while a game runs: the launcher isn't focused, so keys are read from the whole keyboard.</summary>
static class GameInput
{
    public static bool Key(Keys k) => (Win32.GetAsyncKeyState((int)k) & 0x8000) != 0;
}
