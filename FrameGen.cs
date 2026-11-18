using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

// RIFE 4.22 lite (MIT, hzwer / vs-rife weights) on DirectML: two half-size RGBA frames in, flow + blend mask out.
// The picture itself is warped and blended at full size by Renderer.Interpolate.
sealed class FrameGen : IDisposable
{
    public const string ModelFile = "rife422lite_flow_960x544.onnx";
    const int W = Renderer.HalfW, H = Renderer.HalfH;
    readonly InferenceSession session;
    readonly RunOptions run = new();
    readonly byte[][] half = [new byte[W * H * 4], new byte[W * H * 4]];
    readonly OrtValue[] halfIn;
    readonly Float16[] flow = new Float16[W * H * 4], mask = new Float16[W * H];
    readonly OrtValue flowOut, maskOut;
    int cur; // index of the newest frame in half[]
    double diffAvg = -1;

    public FrameGen(int adapterIndex)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "models", ModelFile);
        if (!File.Exists(path)) throw new FileNotFoundException("frame generation model missing: models\\" + ModelFile);
        using var so = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        so.AppendExecutionProvider_DML(adapterIndex);
        session = new InferenceSession(path, so);
        long[] img = [1, H, W, 4];
        halfIn = [OrtValue.CreateTensorValueFromMemory(half[0], img), OrtValue.CreateTensorValueFromMemory(half[1], img)];
        flowOut = OrtValue.CreateTensorValueFromMemory(flow, [1, H, W, 4]);
        maskOut = OrtValue.CreateTensorValueFromMemory(mask, [1, H, W, 1]);
        for (int i = 0; i < 3; i++) Run(); // first runs compile the DirectML graph
    }

    // Buffer for the next real frame's half-size copy; it becomes the newest one
    public byte[] Next() { cur ^= 1; return half[cur]; }

    public ReadOnlySpan<byte> Flow => MemoryMarshal.AsBytes(flow.AsSpan());
    public ReadOnlySpan<byte> Mask => MemoryMarshal.AsBytes(mask.AsSpan());

    public void Run() => session.Run(run, ["a", "b"], [halfIn[cur ^ 1], halfIn[cur]], ["flow", "mask"], [flowOut, maskOut]);

    // Scene cut: a sudden jump in frame difference (fast camera motion is large but not sudden). Mean absolute difference
    // of sampled RGB bytes, 0-255, against a running average of recent pairs.
    public bool IsCut()
    {
        byte[] a = half[cur ^ 1], b = half[cur];
        long sum = 0; int n = 0;
        for (int i = 0; i < a.Length; i += 13 * 4) // every 13th pixel, R G B
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            n += 3;
        }
        double d = (double)sum / n;
        bool cut = diffAvg >= 0 && d > 40 && d > diffAvg * 4;
        if (!cut) diffAvg = diffAvg < 0 ? d : diffAvg * 0.8 + d * 0.2;
        return cut;
    }

    public void Dispose()
    {
        session.Dispose(); run.Dispose();
        foreach (var v in halfIn) v.Dispose();
        flowOut.Dispose(); maskOut.Dispose();
    }
}
