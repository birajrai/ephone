using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace EPhone.Engine;

// 16kHz mono Int16 playback to a named output (CABLE Input on Win, null-sink on Linux).
public sealed class MicOut : IDisposable
{
    private static bool _init;
    private static readonly object _lock = new();
    private PaStream? _stream;
    private readonly ConcurrentQueue<short[]> _q = new();
    private readonly PaStream.Callback _cb;
    private bool _open;

    public MicOut() => _cb = OnCallback;

    public static void InitOnce()
    {
        lock (_lock)
        {
            if (_init) return;
            PortAudio.LoadNativeLibrary();
            PortAudio.Initialize();
            _init = true;
        }
    }

    public static List<(int Index, string Name)> Outputs()
    {
        InitOnce();
        var list = new List<(int, string)>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            if (d.maxOutputChannels > 0) list.Add((i, d.name));
        }
        return list;
    }

    public void Open(int deviceIndex)
    {
        InitOnce();
        Close();
        var outParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = 0.05,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _stream = new PaStream(null, outParams, 16000, 320, StreamFlags.NoFlag, _cb, null);
        _stream.Start();
        _open = true;
    }

    public void Write(short[] pcm) { if (_open) _q.Enqueue(pcm); }

    private StreamCallbackResult OnCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int bytes = (int)frameCount * 2;
        int off = 0;
        while (off < bytes && _q.TryDequeue(out var chunk))
        {
            int n = Math.Min(chunk.Length * 2, bytes - off);
            Marshal.Copy(chunk, 0, output + off, n / 2);
            off += n;
            if (n < chunk.Length * 2)
            {
                // leftover: keep remainder for next callback
                var rest = new short[(chunk.Length * 2 - n) / 2];
                Buffer.BlockCopy(chunk, n, rest, 0, rest.Length * 2);
                var tmp = new ConcurrentQueue<short[]>(_q);
                _q.Clear();
                _q.Enqueue(rest);
                foreach (var t in tmp) _q.Enqueue(t);
            }
        }
        if (off < bytes)
        {
            // underrun: silence
            var zeros = new short[(bytes - off) / 2];
            Marshal.Copy(zeros, 0, output + off, zeros.Length);
        }
        return StreamCallbackResult.Continue;
    }

    public void Close()
    {
        try { _stream?.Stop(); } catch { }
        try { _stream?.Close(); } catch { }
        _stream?.Dispose();
        _stream = null;
        _open = false;
        _q.Clear();
    }

    public void Dispose() => Close();
}
