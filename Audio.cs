using NAudio.CoreAudioApi;
using NAudio.Wave;

// Capture card audio -> speakers/headphones through WASAPI shared mode. Small buffers, newest audio wins (overflow drops the oldest).
// Played by this process, so Discord window share can carry it as application audio.
sealed class AudioBridge : IDisposable
{
    readonly WasapiCapture cap;
    readonly WasapiOut output;
    readonly MMDevice inDev, outDev;
    volatile float peak;
    public float PeakDb => peak <= 0.00001f ? -99f : 20f * MathF.Log10(peak);
    public string Label => $"{inDev.FriendlyName} -> {outDev.FriendlyName}";

    public static string[] Names(DataFlow flow)
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => d.FriendlyName).ToArray();
    }

    // name = part of the endpoint's friendly name; "" output = Windows default
    public AudioBridge(string inName, string outName)
    {
        using var e = new MMDeviceEnumerator();
        inDev = e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Contains(inName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No audio input matching '{inName}'.");
        outDev = outName == "" ? e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Contains(outName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No audio output matching '{outName}'.");

        cap = new WasapiCapture(inDev, true, 20);
        var buf = new BufferedWaveProvider(cap.WaveFormat) { BufferDuration = TimeSpan.FromMilliseconds(80), DiscardOnBufferOverflow = true, ReadFully = true };
        bool flt = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        cap.DataAvailable += (_, a) =>
        {
            buf.AddSamples(a.Buffer, 0, a.BytesRecorded);
            float m = 0;
            if (flt) for (int i = 0; i + 4 <= a.BytesRecorded; i += 4) m = Math.Max(m, Math.Abs(BitConverter.ToSingle(a.Buffer, i)));
            else for (int i = 0; i + 2 <= a.BytesRecorded; i += 2) m = Math.Max(m, Math.Abs(BitConverter.ToInt16(a.Buffer, i) / 32768f));
            peak = m;
        };
        output = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 30);
        output.Init(buf);
        cap.StartRecording();
        output.Play();
    }

    public void Dispose()
    {
        try { cap.StopRecording(); } catch { }
        try { output.Stop(); } catch { }
        cap.Dispose(); output.Dispose(); inDev.Dispose(); outDev.Dispose();
    }
}
