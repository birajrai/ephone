using System.Buffers;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace EPhone.Engine;

// Phone wire client: signal hello, MJPEG video, PCM16 mic audio.
public static class Phone
{
    public static async Task<string> HelloAsync(string ip, CancellationToken ct = default)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(ip, 8080, ct);
        using var r = new StreamReader(c.GetStream(), Encoding.ASCII);
        return (await r.ReadLineAsync(ct)) ?? "";
    }

    // Phone-advertised mode from hello JSON. Missing fields -> 0 (unknown).
    public sealed record HelloInfo(int Width, int Height, int Fps);
    public static HelloInfo ParseHello(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            int g(string n) => r.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
            return new HelloInfo(g("width"), g("height"), g("fps"));
        }
        catch { return new HelloInfo(0, 0, 0); }
    }

    // Ask phone to match our output mode (saves bandwidth). Best-effort, keeps old phones working.
    public static async Task SendVideoConfigAsync(string ip, int w, int h, int fps, int quality = 60,
        CancellationToken ct = default)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(ip, 8080, ct);
            using var s = c.GetStream();
            using var r = new StreamReader(s, Encoding.ASCII, leaveOpen: true);
            await r.ReadLineAsync(ct); // hello
            var msg = $"{{\"type\":\"video\",\"width\":{w},\"height\":{h},\"fps\":{fps},\"quality\":{quality}}}\n";
            var b = Encoding.ASCII.GetBytes(msg);
            await s.WriteAsync(b, ct);
            await s.FlushAsync(ct);
        }
        catch { }
    }

    public sealed class Mjpeg : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private CancellationTokenSource? _cts;
        public event Action<byte[]>? Frame;

        public void Start(string url)
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => Pump(url, _cts.Token));
        }

        public void Stop() => _cts?.Cancel();
        public void Dispose() { Stop(); _http.Dispose(); }

        private async Task Pump(string url, CancellationToken ct)
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            using var s = await res.Content.ReadAsStreamAsync(ct);
            var buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
            int held = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await s.ReadAsync(buf.AsMemory(held, buf.Length - held), ct);
                    if (n == 0) break;
                    held += n;
                    // extract all complete JPEGs (SOI..EOI)
                    int start = 0;
                    while (true)
                    {
                        int soi = IndexOf(buf, 0xFF, 0xD8, start, held);
                        if (soi < 0) break;
                        int eoi = IndexOf(buf, 0xFF, 0xD9, soi + 2, held);
                        if (eoi < 0) break;
                        int len = eoi + 2 - soi;
                        var jpg = new byte[len];
                        Buffer.BlockCopy(buf, soi, jpg, 0, len);
                        Frame?.Invoke(jpg);
                        start = eoi + 2;
                    }
                    if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, held - start); held -= start; }
                    if (held == buf.Length) held = 0; // overflow guard, drop
                }
            }
            catch (OperationCanceledException) { }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        private static int IndexOf(byte[] b, byte a, byte c, int from, int to)
        {
            for (int i = from; i + 1 < to; i++)
                if (b[i] == a && b[i + 1] == c) return i;
            return -1;
        }
    }

    public sealed class Pcm : IDisposable
    {
        private CancellationTokenSource? _cts;
        public event Action<short[]>? Chunk; // 20ms @16k mono

        public void Start(string ip)
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => Pump(ip, _cts.Token));
        }

        public void Stop() => _cts?.Cancel();
        public void Dispose() => Stop();

        private async Task Pump(string ip, CancellationToken ct)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(ip, 8082, ct);
            var s = c.GetStream();
            var hdr = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                await Fill(s, hdr, ct);
                int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                if (len is <= 0 or > 65536) throw new InvalidDataException("bad pcm frame");
                var raw = new byte[len];
                await Fill(s, raw, ct);
                var pcm = new short[len / 2];
                Buffer.BlockCopy(raw, 0, pcm, 0, len);
                Chunk?.Invoke(pcm);
            }
        }

        private static async Task Fill(Stream s, byte[] b, CancellationToken ct)
        {
            int off = 0;
            while (off < b.Length)
            {
                int n = await s.ReadAsync(b.AsMemory(off), ct);
                if (n == 0) throw new IOException("phone closed");
                off += n;
            }
        }
    }
}
