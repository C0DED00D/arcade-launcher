using System.Runtime.InteropServices;
using System.Text;

namespace ArcadeLauncher;

/// <summary>The small slice of OpenGL 3.3 the CRT renderer needs, loaded by hand (no NuGet packages).</summary>
static unsafe class GL
{
    public const uint TEXTURE_2D = 0x0DE1, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803,
        LINEAR = 0x2601, NEAREST = 0x2600, CLAMP_TO_EDGE = 0x812F, RGBA = 0x1908, BGRA = 0x80E1, RGBA16F = 0x881A, RGBA8 = 0x8058,
        UNSIGNED_BYTE = 0x1401, FLOAT = 0x1406, TRIANGLES = 4, COLOR_BUFFER_BIT = 0x4000, FRAGMENT_SHADER = 0x8B30, VERTEX_SHADER = 0x8B31,
        COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5,
        TEXTURE0 = 0x84C0, VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02, UNPACK_ALIGNMENT = 0x0CF5, BLEND = 0x0BE2, DEPTH_TEST = 0x0B71,
        FRAMEBUFFER_SRGB = 0x8DB9;

    [DllImport("opengl32.dll")] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] public static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] public static extern void glGenTextures(int n, uint* tex);
    [DllImport("opengl32.dll")] public static extern void glDeleteTextures(int n, uint* tex);
    [DllImport("opengl32.dll")] public static extern void glBindTexture(uint target, uint tex);
    [DllImport("opengl32.dll")] public static extern void glTexParameteri(uint target, uint name, int value);
    [DllImport("opengl32.dll")] public static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, void* data);
    [DllImport("opengl32.dll")] public static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, void* data);
    [DllImport("opengl32.dll")] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport("opengl32.dll")] public static extern void glPixelStorei(uint name, int value);
    [DllImport("opengl32.dll")] public static extern void glDisable(uint cap);
    [DllImport("opengl32.dll")] public static extern uint glGetError();
    [DllImport("opengl32.dll")] public static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, void* data);
    [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);
    public static string GetString(uint name) => Marshal.PtrToStringAnsi(glGetString(name)) ?? "";

    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint, void> DeleteShader;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    public static delegate* unmanaged<uint, void> UseProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public static delegate* unmanaged<uint, void> ActiveTexture;
    public static delegate* unmanaged<int, uint*, void> GenVertexArrays;
    public static delegate* unmanaged<uint, void> BindVertexArray;
    public static delegate* unmanaged<int, int> SwapIntervalEXT;

    static IntPtr Load(string name)
    {
        IntPtr p = Win32.wglGetProcAddress(name);
        long v = p.ToInt64();
        if (v is 0 or 1 or 2 or 3 or -1) throw new Exception("OpenGL function missing: " + name);
        return p;
    }

    public static void LoadAll()
    {
        CreateShader = (delegate* unmanaged<uint, uint>)Load("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Load("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)Load("glCompileShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Load("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load("glGetShaderInfoLog");
        DeleteShader = (delegate* unmanaged<uint, void>)Load("glDeleteShader");
        CreateProgram = (delegate* unmanaged<uint>)Load("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)Load("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)Load("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Load("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged<uint, void>)Load("glUseProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)Load("glGetUniformLocation");
        Uniform1i = (delegate* unmanaged<int, int, void>)Load("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)Load("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)Load("glUniform2f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Load("glUniform4f");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)Load("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)Load("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)Load("glFramebufferTexture2D");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)Load("glCheckFramebufferStatus");
        ActiveTexture = (delegate* unmanaged<uint, void>)Load("glActiveTexture");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)Load("glGenVertexArrays");
        BindVertexArray = (delegate* unmanaged<uint, void>)Load("glBindVertexArray");
        try { SwapIntervalEXT = (delegate* unmanaged<int, int>)Load("wglSwapIntervalEXT"); } catch { SwapIntervalEXT = null; }
    }

    public static uint Compile(string vs, string fs, string name)
    {
        uint v = Shader(VERTEX_SHADER, vs, name), f = Shader(FRAGMENT_SHADER, fs, name);
        uint p = CreateProgram();
        AttachShader(p, v); AttachShader(p, f); LinkProgram(p);
        int ok; GetProgramiv(p, LINK_STATUS, &ok);
        if (ok == 0)
        {
            byte* buf = stackalloc byte[4096]; int len;
            GetProgramInfoLog(p, 4096, &len, buf);
            throw new Exception($"Shader {name} link failed: " + Encoding.ASCII.GetString(buf, len));
        }
        DeleteShader(v); DeleteShader(f);
        return p;
    }

    static uint Shader(uint type, string src, string name)
    {
        uint s = CreateShader(type);
        byte[] bytes = Encoding.ASCII.GetBytes(src);
        fixed (byte* b = bytes)
        {
            byte* pb = b; int len = bytes.Length;
            ShaderSource(s, 1, &pb, &len);
        }
        CompileShader(s);
        int ok; GetShaderiv(s, COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* buf = stackalloc byte[8192]; int len;
            GetShaderInfoLog(s, 8192, &len, buf);
            throw new Exception($"Shader {name} compile failed: " + Encoding.ASCII.GetString(buf, len));
        }
        return s;
    }

    public static int Uniform(uint prog, string name)
    {
        byte[] b = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) return GetUniformLocation(prog, p);
    }

    public static uint NewTexture(int w, int h, uint internalFormat, bool linear)
    {
        uint t; glGenTextures(1, &t);
        glBindTexture(TEXTURE_2D, t);
        glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)(linear ? LINEAR : NEAREST));
        glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)(linear ? LINEAR : NEAREST));
        glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE);
        glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
        glTexImage2D(TEXTURE_2D, 0, (int)internalFormat, w, h, 0, RGBA, FLOAT, null);
        return t;
    }

    public static uint NewFramebuffer(uint tex)
    {
        uint fb; GenFramebuffers(1, &fb);
        BindFramebuffer(FRAMEBUFFER, fb);
        FramebufferTexture2D(FRAMEBUFFER, COLOR_ATTACHMENT0, TEXTURE_2D, tex, 0);
        uint st = CheckFramebufferStatus(FRAMEBUFFER);
        if (st != FRAMEBUFFER_COMPLETE) throw new Exception($"Framebuffer incomplete: 0x{st:X}");
        BindFramebuffer(FRAMEBUFFER, 0);
        return fb;
    }
}
