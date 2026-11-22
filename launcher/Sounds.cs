using System.Media;

namespace ArcadeLauncher;

/// <summary>Little square-wave blips made on the fly (no sound files to ship).</summary>
sealed class Sounds
{
    readonly SoundPlayer _move, _coin, _back, _buzz, _toggle;
    public bool Enabled { get; set; } = true;

    public Sounds()
    {
        _move = Make((1400, 0.022, 0.18));
        _coin = Make((988, 0.07, 0.25), (1319, 0.38, 0.25));
        _back = Make((660, 0.05, 0.2), (440, 0.08, 0.2));
        _buzz = Make((110, 0.22, 0.3));
        _toggle = Make((880, 0.04, 0.2), (1760, 0.05, 0.2));
    }

    public void Move() => Play(_move);
    public void Coin() => Play(_coin);
    public void Back() => Play(_back);
    public void Buzz() => Play(_buzz);
    public void Toggle() => Play(_toggle);

    void Play(SoundPlayer p) { if (!Enabled) return; try { p.Play(); } catch { } }

    static SoundPlayer Make(params (double hz, double secs, double vol)[] notes)
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (hz, secs, vol) in notes)
        {
            int n = (int)(secs * rate);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, i / 60.0) * Math.Pow(1 - i / (double)n, 1.5);
                double sq = Math.Sin(2 * Math.PI * hz * i / rate) >= 0 ? 1 : -1;
                samples.Add((short)(sq * env * vol * 32767 * 0.6));
            }
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        ms.Position = 0;
        var p = new SoundPlayer(ms);
        p.Load();
        return p;
    }
}
