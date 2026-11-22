using System.Drawing;
using System.Drawing.Imaging;

namespace ArcadeLauncher;

/// <summary>A picture held as 0xAARRGGBB pixels.</summary>
sealed class Img
{
    public readonly int W, H;
    public readonly uint[] Pix;
    public Img(int w, int h) { W = w; H = h; Pix = new uint[w * h]; }

    public static Img Load(string path)
    {
        using var bmp = new Bitmap(path);
        var img = new Img(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < img.H; y++)
            {
                uint* row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                for (int x = 0; x < img.W; x++) img.Pix[y * img.W + x] = row[x];
            }
        }
        bmp.UnlockBits(data);
        return img;
    }

    /// <summary>How busy the picture is (0..1): used to skip blank/black attract frames.</summary>
    public double Interest()
    {
        double sum = 0, sum2 = 0; int n = 0;
        var colours = new HashSet<uint>();
        for (int i = 0; i < Pix.Length; i += 7)
        {
            uint c = Pix[i];
            double l = (((c >> 16) & 255) * 0.3 + ((c >> 8) & 255) * 0.59 + (c & 255) * 0.11) / 255.0;
            sum += l; sum2 += l * l; n++;
            if (colours.Count < 400) colours.Add(c & 0xF0F0F0);
        }
        double mean = sum / n, var = sum2 / n - mean * mean;
        return Math.Sqrt(Math.Max(var, 0)) * 2 + mean * 0.5 + colours.Count / 400.0;
    }
}

/// <summary>The menu's frame buffer and drawing helpers (everything is drawn on the CPU at arcade resolution).</summary>
sealed class Canvas
{
    public readonly int W, H;
    public readonly uint[] Pix;
    public Canvas(int w, int h) { W = w; H = h; Pix = new uint[w * h]; }

    public static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)(Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255));

    public static uint Hsv(float h, float s, float v)
    {
        h = (h % 1 + 1) % 1 * 6; int i = (int)h; float f = h - i;
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Rgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    public static uint Mix(uint a, uint b, float t)
    {
        int ia = (int)(t * 256); int na = 256 - ia;
        uint r = (((a >> 16) & 255) * (uint)na + ((b >> 16) & 255) * (uint)ia) >> 8;
        uint g = (((a >> 8) & 255) * (uint)na + ((b >> 8) & 255) * (uint)ia) >> 8;
        uint bl = ((a & 255) * (uint)na + (b & 255) * (uint)ia) >> 8;
        return 0xFF000000u | r << 16 | g << 8 | bl;
    }

    public void Clear(uint c) => Array.Fill(Pix, c);

    public void Plot(int x, int y, uint c, float a = 1)
    {
        if ((uint)x >= W || (uint)y >= H) return;
        int i = y * W + x;
        Pix[i] = a >= 1 ? c : Mix(Pix[i], c, a);
    }

    public void Fill(int x, int y, int w, int h, uint c, float a = 1)
    {
        int x0 = Math.Max(x, 0), y0 = Math.Max(y, 0), x1 = Math.Min(x + w, W), y1 = Math.Min(y + h, H);
        if (x1 <= x0 || y1 <= y0) return; // wholly off the canvas (e.g. marquee text scrolled past the edge)
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W;
            if (a >= 1) Array.Fill(Pix, c, row + x0, Math.Max(0, x1 - x0));
            else for (int xx = x0; xx < x1; xx++) Pix[row + xx] = Mix(Pix[row + xx], c, a);
        }
    }

    public void Frame(int x, int y, int w, int h, uint c, int t = 1)
    {
        Fill(x, y, w, t, c); Fill(x, y + h - t, w, t, c);
        Fill(x, y, t, h, c); Fill(x + w - t, y, t, h, c);
    }

    /// <summary>Draws a picture scaled into a rectangle (bilinear; a CRT never shows hard pixel edges anyway).</summary>
    public void Draw(Img img, int dx, int dy, int dw, int dh, float alpha = 1)
    {
        if (dw <= 0 || dh <= 0) return;
        float sx = (float)img.W / dw, sy = (float)img.H / dh;
        for (int y = 0; y < dh; y++)
        {
            int ty = dy + y; if ((uint)ty >= H) continue;
            float fy = Math.Clamp((y + 0.5f) * sy - 0.5f, 0, img.H - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, img.H - 1); float wy = fy - y0;
            for (int x = 0; x < dw; x++)
            {
                int tx = dx + x; if ((uint)tx >= W) continue;
                float fx = Math.Clamp((x + 0.5f) * sx - 0.5f, 0, img.W - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, img.W - 1); float wx = fx - x0;
                uint top = Mix(img.Pix[y0 * img.W + x0], img.Pix[y0 * img.W + x1], wx);
                uint bot = Mix(img.Pix[y1 * img.W + x0], img.Pix[y1 * img.W + x1], wx);
                uint c = Mix(top, bot, wy);
                int i = ty * W + tx;
                Pix[i] = alpha >= 1 ? c : Mix(Pix[i], c, alpha);
            }
        }
    }
}

/// <summary>MAME's own "uismall" BDF bitmap font (CC0), drawn at whole-pixel scales for that arcade look.</summary>
sealed class PixelFont
{
    sealed class Glyph { public int Adv, W, H, Xo, Yo; public uint[] Rows = Array.Empty<uint>(); }
    readonly Dictionary<int, Glyph> _g = new();
    public readonly int Ascent = 9, Height = 11;

    public PixelFont(string bdfPath)
    {
        Glyph? cur = null; int enc = -1; int row = -1;
        foreach (var raw in File.ReadLines(bdfPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("FONT_ASCENT ")) Ascent = int.Parse(line[12..]);
            else if (line.StartsWith("STARTCHAR")) { cur = new Glyph(); enc = -1; row = -1; }
            else if (cur == null) continue;
            else if (line.StartsWith("ENCODING ")) enc = int.Parse(line[9..]);
            else if (line.StartsWith("DWIDTH ")) cur.Adv = int.Parse(line.Split(' ')[1]);
            else if (line.StartsWith("BBX "))
            {
                var p = line.Split(' ');
                cur.W = int.Parse(p[1]); cur.H = int.Parse(p[2]); cur.Xo = int.Parse(p[3]); cur.Yo = int.Parse(p[4]);
                cur.Rows = new uint[cur.H];
            }
            else if (line == "BITMAP") row = 0;
            else if (line == "ENDCHAR") { if (enc >= 0) _g[enc] = cur; cur = null; }
            else if (row >= 0 && row < cur.Rows.Length)
            {
                uint bits = Convert.ToUInt32(line, 16);
                cur.Rows[row++] = bits << (32 - line.Length * 4); // left-align the bits
            }
        }
    }

    Glyph? Get(char ch) => _g.TryGetValue(ch, out var g) ? g : _g.GetValueOrDefault('?');

    public int Measure(string s, int scale) { int w = 0; foreach (var ch in s) w += (Get(ch)?.Adv ?? 6) * scale; return w; }

    public void Draw(Canvas c, string s, int x, int y, int scale, uint color, float alpha = 1, uint? shadow = null)
    {
        if (shadow is uint sh) Draw(c, s, x + scale, y + scale, scale, sh, alpha);
        int pen = x;
        foreach (var ch in s)
        {
            var g = Get(ch);
            if (g == null) { pen += 6 * scale; continue; }
            int top = y + (Ascent - (g.H + g.Yo)) * scale;
            for (int r = 0; r < g.H; r++)
            {
                uint bits = g.Rows[r];
                for (int b = 0; b < g.W; b++)
                    if ((bits & (0x80000000u >> b)) != 0)
                        c.Fill(pen + (g.Xo + b) * scale, top + r * scale, scale, scale, color, alpha);
            }
            pen += g.Adv * scale;
        }
    }

    public void DrawCentered(Canvas c, string s, int cx, int y, int scale, uint color, float alpha = 1, uint? shadow = null)
        => Draw(c, s, cx - Measure(s, scale) / 2, y, scale, color, alpha, shadow);
}
