using System.Reflection;

namespace ArcadeLauncher;

/// <summary>Look of the menu's simulated CRT. Saved in config\settings.json.</summary>
public class CrtSettings
{
    public float Lines { get; set; } = 240;          // 240 = 15 kHz arcade monitor, 480 = 31 kHz
    public float Curvature { get; set; } = 0.045f;
    public float Scanlines { get; set; } = 1.0f;
    public float Mask { get; set; } = 0.38f;
    public float Halation { get; set; } = 0.22f;
    public float Glow { get; set; } = 0.14f;
    public float Convergence { get; set; } = 0.35f;
    public float Noise { get; set; } = 0.010f;
    public float Brightness { get; set; } = 1.30f;
    public float Persistence { get; set; } = 0.45f;
    public float BezelReflection { get; set; } = 0.55f;
    public float CornerRadius { get; set; } = 0.045f;
    public float PictureHeight { get; set; } = 0.90f;  // fraction of the window height the tube fills
}

/// <summary>Draws a CPU-rendered frame (BGRA) onto the window through the CRT shader chain.</summary>
unsafe sealed class CrtRenderer : IDisposable
{
    readonly IntPtr _hwnd, _hdc, _ctx;
    readonly int _sw, _sh;
    readonly uint _progPhos, _progBlur, _progCrt, _vao;
    readonly uint _srcTex;
    readonly uint[] _phosTex = new uint[2], _phosFb = new uint[2];
    readonly uint[] _blurTex = new uint[6], _blurFb = new uint[6];
    readonly (int w, int h)[] _blurSize;
    int _phosIndex;
    public string Info { get; }

    public CrtRenderer(IntPtr hwnd, int srcW, int srcH)
    {
        _hwnd = hwnd; _sw = srcW; _sh = srcH;
        _hdc = Win32.GetDC(hwnd);
        var pfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)sizeof(Win32.PIXELFORMATDESCRIPTOR), nVersion = 1,
            dwFlags = Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER,
            iPixelType = 0, cColorBits = 32, cAlphaBits = 8, cDepthBits = 0,
        };
        int fmt = Win32.ChoosePixelFormat(_hdc, ref pfd);
        if (fmt == 0 || !Win32.SetPixelFormat(_hdc, fmt, ref pfd)) throw new Exception("No OpenGL pixel format");
        _ctx = Win32.wglCreateContext(_hdc);
        if (_ctx == IntPtr.Zero || !Win32.wglMakeCurrent(_hdc, _ctx)) throw new Exception("Could not create an OpenGL context");
        GL.LoadAll();
        Info = $"{GL.GetString(GL.RENDERER)} / OpenGL {GL.GetString(GL.VERSION)}";
        if (GL.SwapIntervalEXT != null) GL.SwapIntervalEXT(1);

        string vs = Res("fullscreen.vert.glsl");
        _progPhos = GL.Compile(vs, Res("phosphor.glsl"), "phosphor");
        _progBlur = GL.Compile(vs, Res("blur.glsl"), "blur");
        _progCrt = GL.Compile(vs, Res("crt.glsl"), "crt");
        uint vao; GL.GenVertexArrays(1, &vao); _vao = vao;
        GL.BindVertexArray(_vao);
        GL.glDisable(GL.DEPTH_TEST);
        GL.glDisable(GL.BLEND);

        _srcTex = GL.NewTexture(srcW, srcH, GL.RGBA8, true);
        for (int i = 0; i < 2; i++)
        {
            _phosTex[i] = GL.NewTexture(srcW, srcH, GL.RGBA16F, true);
            _phosFb[i] = GL.NewFramebuffer(_phosTex[i]);
        }
        // Blur chain: 1/2 (h, v), 1/4 (h, v), 1/8 (h, v)
        _blurSize = new[] { (srcW / 2, srcH / 2), (srcW / 2, srcH / 2), (srcW / 4, srcH / 4), (srcW / 4, srcH / 4), (srcW / 8, srcH / 8), (srcW / 8, srcH / 8) };
        for (int i = 0; i < 6; i++)
        {
            _blurTex[i] = GL.NewTexture(_blurSize[i].w, _blurSize[i].h, GL.RGBA16F, true);
            _blurFb[i] = GL.NewFramebuffer(_blurTex[i]);
        }
        foreach (var fb in _phosFb) { GL.BindFramebuffer(GL.FRAMEBUFFER, fb); GL.glClearColor(0, 0, 0, 1); GL.glClear(GL.COLOR_BUFFER_BIT); }
        GL.BindFramebuffer(GL.FRAMEBUFFER, 0);
    }

    static string Res(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new Exception("missing shader " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static void Bind(int unit, uint tex) { GL.ActiveTexture(GL.TEXTURE0 + (uint)unit); GL.glBindTexture(GL.TEXTURE_2D, tex); }

    public void Render(uint[] pixels, int winW, int winH, float time, float power, CrtSettings s, bool crtOn)
    {
        // Upload the menu frame
        GL.ActiveTexture(GL.TEXTURE0);
        GL.glBindTexture(GL.TEXTURE_2D, _srcTex);
        GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 4);
        fixed (uint* p = pixels) GL.glTexSubImage2D(GL.TEXTURE_2D, 0, 0, 0, _sw, _sh, GL.BGRA, GL.UNSIGNED_BYTE, p);

        // 1. Phosphor: linearise and keep a decaying trail
        int cur = _phosIndex, prev = 1 - _phosIndex;
        _phosIndex = prev;
        GL.BindFramebuffer(GL.FRAMEBUFFER, _phosFb[cur]);
        GL.glViewport(0, 0, _sw, _sh);
        GL.UseProgram(_progPhos);
        Bind(0, _srcTex); Bind(1, _phosTex[prev]);
        GL.Uniform1i(GL.Uniform(_progPhos, "src"), 0);
        GL.Uniform1i(GL.Uniform(_progPhos, "prev"), 1);
        GL.Uniform1f(GL.Uniform(_progPhos, "decay"), crtOn ? s.Persistence : 0f);
        GL.Uniform1f(GL.Uniform(_progPhos, "gamma"), 2.2f);
        GL.glDrawArrays(GL.TRIANGLES, 0, 3);

        // 2. Blur chain for halation / glow
        GL.UseProgram(_progBlur);
        GL.Uniform1i(GL.Uniform(_progBlur, "src"), 0);
        int dirLoc = GL.Uniform(_progBlur, "dir");
        uint input = _phosTex[cur];
        int inW = _sw, inH = _sh;
        for (int i = 0; i < 6; i++)
        {
            var (w, h) = _blurSize[i];
            GL.BindFramebuffer(GL.FRAMEBUFFER, _blurFb[i]);
            GL.glViewport(0, 0, w, h);
            Bind(0, input);
            bool horiz = i % 2 == 0;
            GL.Uniform2f(dirLoc, horiz ? 1.0f / inW * 1.2f : 0, horiz ? 0 : 1.0f / inH * 1.2f);
            GL.glDrawArrays(GL.TRIANGLES, 0, 3);
            input = _blurTex[i]; inW = w; inH = h;
        }

        // 3. CRT to the window
        GL.BindFramebuffer(GL.FRAMEBUFFER, 0);
        GL.glViewport(0, 0, winW, winH);
        GL.glClearColor(0, 0, 0, 1);
        GL.glClear(GL.COLOR_BUFFER_BIT);
        GL.UseProgram(_progCrt);
        Bind(0, _phosTex[cur]); Bind(1, _blurTex[1]); Bind(2, _blurTex[5]);
        GL.Uniform1i(GL.Uniform(_progCrt, "src"), 0);
        GL.Uniform1i(GL.Uniform(_progCrt, "bloomA"), 1);
        GL.Uniform1i(GL.Uniform(_progCrt, "bloomB"), 2);
        GL.Uniform2f(GL.Uniform(_progCrt, "srcSize"), _sw, _sh);
        float th = winH * s.PictureHeight, tw = th * _sw / _sh;
        if (tw > winW * 0.96f) { tw = winW * 0.96f; th = tw * _sh / _sw; }
        GL.Uniform4f(GL.Uniform(_progCrt, "tube"), (winW - tw) / 2, (winH - th) / 2, tw, th);
        GL.Uniform1f(GL.Uniform(_progCrt, "time"), time);
        GL.Uniform1f(GL.Uniform(_progCrt, "power"), power);
        // Lines is for a 480-row picture (240 = one scanline per 2 rows, a font pixel at scale 2): keep that ratio on the taller canvas
        GL.Uniform1f(GL.Uniform(_progCrt, "lines"), crtOn ? s.Lines * _sh / 480f : _sh);
        GL.Uniform2f(GL.Uniform(_progCrt, "warp"), crtOn ? s.Curvature : 0, crtOn ? s.Curvature * 1.35f : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "scanStr"), crtOn ? s.Scanlines : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "maskStr"), crtOn ? s.Mask : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "maskPitch"), MathF.Max(1, MathF.Round(th / 1080f)));
        GL.Uniform1f(GL.Uniform(_progCrt, "halation"), crtOn ? s.Halation : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "glow"), crtOn ? s.Glow : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "conv"), crtOn ? s.Convergence : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "noiseAmt"), crtOn ? s.Noise : 0);
        GL.Uniform1f(GL.Uniform(_progCrt, "bright"), crtOn ? s.Brightness : 1);
        GL.Uniform1f(GL.Uniform(_progCrt, "cornerR"), crtOn ? s.CornerRadius : 0.001f);
        GL.Uniform1f(GL.Uniform(_progCrt, "bezelRefl"), crtOn ? s.BezelReflection : 0);
        GL.glDrawArrays(GL.TRIANGLES, 0, 3);
        Win32.SwapBuffers(_hdc);
    }

    public void Dispose()
    {
        Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        Win32.wglDeleteContext(_ctx);
        Win32.ReleaseDC(_hwnd, _hdc);
    }
}
