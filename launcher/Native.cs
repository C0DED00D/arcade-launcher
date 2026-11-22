using System.Runtime.InteropServices;

namespace ArcadeLauncher;

/// <summary>Win32, WGL and XInput calls.</summary>
static unsafe class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift,
            cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    public const uint PFD_DRAW_TO_WINDOW = 4, PFD_SUPPORT_OPENGL = 0x20, PFD_DOUBLEBUFFER = 1;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern IntPtr wglGetProcAddress(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern int ShowCursor(bool show);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    public const uint WM_CLOSE = 0x0010;

    /// <summary>Top-level visible windows of a process.</summary>
    public static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Lets this process take the foreground back (Windows blocks it otherwise after another app had it).</summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);      // Alt down/up: the documented trick that unlocks SetForegroundWindow
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(hWnd, 5);
        SetForegroundWindow(hWnd);
    }
}

/// <summary>XInput pads, including the Guide button (hidden ordinal 100 in xinput1_4).</summary>
static unsafe class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public Gamepad Pad; }

    public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LThumb = 0x40, RThumb = 0x80,
        LB = 0x100, RB = 0x200, Guide = 0x400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    static readonly delegate* unmanaged<uint, State*, uint> _get;
    public static readonly bool Available;
    public static readonly bool HasGuide;

    static XInput()
    {
        IntPtr lib = Win32.LoadLibrary("xinput1_4.dll");
        if (lib == IntPtr.Zero) lib = Win32.LoadLibrary("xinput1_3.dll");
        if (lib == IntPtr.Zero) return;
        IntPtr p = Win32.GetProcAddress(lib, (IntPtr)100); // XInputGetStateEx (reports Guide)
        HasGuide = p != IntPtr.Zero;
        if (p == IntPtr.Zero) p = Win32.GetProcAddress(lib, "XInputGetState");
        if (p == IntPtr.Zero) return;
        _get = (delegate* unmanaged<uint, State*, uint>)p;
        Available = true;
    }

    // Polling an unplugged pad is slow (it scans for the device), so empty slots are only rechecked every 2 s.
    static readonly long[] _nextProbe = new long[4];

    public static bool TryGet(int index, out Gamepad pad)
    {
        pad = default;
        if (!Available) return false;
        long now = Environment.TickCount64;
        if (now < _nextProbe[index]) return false;
        State s;
        if (_get((uint)index, &s) != 0) { _nextProbe[index] = now + 2000; return false; }
        pad = s.Pad;
        return true;
    }
}
