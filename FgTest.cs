using System.Diagnostics;
using System.Runtime.InteropServices;

// --fgtest <dir> <name>: reads <name>_0.yuv, <name>_1.yuv, <name>_2.yuv (1920x1080 YUY2: A, the true middle, B), runs the
// frame generation path and prints PSNR of the in-between frame against the true middle, next to a 50/50 blend and a repeat
// (what a 30 fps game shows), plus the time per in-between frame. Writes <name>_mid.rgba for a look.
static class FgTest
{
    const int W = 1920, H = 1080;

    public static unsafe void Run(string dir, string name, bool steady)
    {
        using var form = new Form { ShowInTaskbar = false, Width = 320, Height = 180 };
        var r = new Renderer(form.Handle, 320, 180);
        var fg = new FrameGen(r.AdapterIndex());
        byte[] Load(int k) => File.ReadAllBytes(Path.Combine(dir, $"{name}_{k}.yuv"));
        byte[] a = Load(0), t = Load(1), b = Load(2);
        var truth = new byte[W * H * 4]; var ra = new byte[W * H * 4]; var rb = new byte[W * H * 4]; var mid = new byte[W * H * 4];
        void Feed(byte[] f) { fixed (byte* p = f) r.NewFrame((IntPtr)p, W * 2, W, H, true, fg.Next()); }

        Feed(t); r.ReadRgb(1, truth);
        Feed(a); Feed(b);
        r.ReadRgb(0, ra); r.ReadRgb(1, rb);
        fg.Run(); r.Interpolate(fg.Flow, fg.Mask, steady); r.ReadRgb(2, mid);
        var blend = new byte[W * H * 4];
        for (int i = 0; i < blend.Length; i++) blend[i] = (byte)((ra[i] + rb[i] + 1) / 2);
        File.WriteAllBytes(Path.Combine(dir, $"{name}_mid.rgba"), mid);

        // timing: the live path per in-between frame (upload + convert + half readback + model + warp)
        var ts = new List<double>(); var tf = new List<double>(); var tm = new List<double>(); var tw = new List<double>();
        double Ms(long s) => (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
        for (int i = 0; i < 120; i++)
        {
            long s = Stopwatch.GetTimestamp();
            Feed(i % 2 == 0 ? a : b); tf.Add(Ms(s));
            long s1 = Stopwatch.GetTimestamp(); fg.Run(); tm.Add(Ms(s1));
            long s2 = Stopwatch.GetTimestamp(); r.Interpolate(fg.Flow, fg.Mask, steady); r.Sync(); tw.Add(Ms(s2));
            ts.Add(Ms(s));
            Thread.Sleep(16);
        }
        static string P(List<double> l) { l.Sort(); return $"{l[60]:F1}/{l[108]:F1}"; }
        string msg = $"{name}: RIFE {Psnr(mid, truth):F2} dB   blend {Psnr(blend, truth):F2} dB   repeat {Psnr(ra, truth):F2} dB   " +
                     $"ms p50/p90: total {P(ts)}  frame in {P(tf)}  model {P(tm)}  warp {P(tw)}";
        Console.WriteLine(msg);
        File.AppendAllText(Path.Combine(dir, "results.txt"), msg + Environment.NewLine);
        fg.Dispose(); r.Dispose();
    }

    // PSNR over RGB, ignoring a 16 px border (clamped edges of the pan are not motion)
    static double Psnr(byte[] x, byte[] y)
    {
        double se = 0; long n = 0;
        for (int row = 16; row < H - 16; row++)
            for (int col = 16; col < W - 16; col++)
            {
                int i = (row * W + col) * 4;
                for (int c = 0; c < 3; c++) { double d = x[i + c] - y[i + c]; se += d * d; }
                n += 3;
            }
        return 10 * Math.Log10(255.0 * 255.0 / Math.Max(se / n, 1e-9));
    }
}
