using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace EPhone.Engine;

// Shared-mem video feeder for driver/cam/EPhoneCam.dll.
// Layout: [CamHeader 24B][NV12 up to 1920x1080]. Writer = this. Reader = EPhoneCam Pump.
public sealed class FrameServer : IDisposable
{
    public static readonly (int W, int H)[] Modes = [(640, 480), (1280, 720), (1920, 1080)];
    public static readonly int[] FpsModes = [15, 20, 30];
    public const string MapName = "Local\\EPhoneCam";
    private const int HeaderBytes = 24;
    private const int MaxW = 1920, MaxH = 1080;
    private const long MaxBytes = 1920L * 1080 * 3 / 2;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private ulong _frameId;
    private long _lastPushMs;
    private bool _ready;

    public int Fps { get; }
    public bool Ready => _ready;
    public int LastW { get; private set; }
    public int LastH { get; private set; }

    public FrameServer(int fps = 30)
    {
        Fps = Math.Clamp(fps, 5, 60);
    }

    public void Open()
    {
        try
        {
            Dispose();
            _mmf = MemoryMappedFile.CreateOrOpen(MapName, HeaderBytes + MaxBytes);
            _acc = _mmf.CreateViewAccessor();
            _ready = true;
        }
        catch { _ready = false; }
    }

    // Phone gives MJPEG JPEGs. Decode -> resize to W/H -> NV12 -> shared mem. FPS-throttled.
    public unsafe void PushJpeg(byte[] jpg)
    {
        if (!_ready || _acc is null) return;
        long now = Environment.TickCount64;
        if (now - _lastPushMs < 1000 / Fps) return; // drop to target fps
        try
        {
            using var bmp = SKBitmap.Decode(jpg);
            if (bmp is null) return;
            // Native phone quality: no forced resize. Shrink only if over driver slot.
            using var down = (bmp.Width > MaxW || bmp.Height > MaxH)
                ? FitInside(bmp, MaxW, MaxH) : null;
            var src = down ?? bmp;
            int w = src.Width & ~1; // NV12 needs even dims
            int h = src.Height & ~1;
            if (w < 320 || h < 240 || (long)w * h * 3 / 2 > MaxBytes) return;
            // BGRA pixels via SKImage.ReadPixels
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = new byte[w * h * 4];
            using var img = SKImage.FromBitmap(src);
            fixed (byte* p = pixels)
            {
                if (!img.ReadPixels(info, (IntPtr)p, w * 4)) return;
            }
            // BGRA -> NV12 studio-range, then write to MMF
            var nv12 = new byte[w * h * 3 / 2];
            BgraToNv12(pixels, nv12, w, h);
            _frameId++;
            _lastPushMs = now;
            LastW = w; LastH = h;
            _acc.Write(0, (uint)w);
            _acc.Write(4, (uint)h);
            _acc.Write(8, (uint)Fps);
            _acc.Write(12, (uint)0x3231564E); // 'NV12'
            _acc.Write(16, _frameId);
            _acc.WriteArray(HeaderBytes, nv12, 0, nv12.Length);
        }
        catch { }
    }

    private static SKBitmap? FitInside(SKBitmap bmp, int maxW, int maxH)
    {
        float s = Math.Min(maxW / (float)bmp.Width, maxH / (float)bmp.Height);
        int w = Math.Max(2, ((int)(bmp.Width * s)) & ~1);
        int h = Math.Max(2, ((int)(bmp.Height * s)) & ~1);
        return bmp.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
    }

    // BT.601 studio-range, integer math, 2x2 UV average. Old float full-range
    // version washed colors out + sampled one pixel per UV pair (chroma aliasing).
    private static unsafe void BgraToNv12(byte[] bgra, byte[] nv12, int w, int h)
    {
        fixed (byte* src = bgra, dst = nv12)
        {
            byte* yPlane = dst;
            byte* uvPlane = dst + w * h;
            for (int y = 0; y < h; y += 2)
            {
                byte* s0 = src + y * w * 4;
                byte* s1 = s0 + w * 4;
                byte* y0 = yPlane + y * w;
                byte* y1 = y0 + w;
                byte* uv = uvPlane + (y / 2) * w;
                int yLast = y + 1 < h ? 1 : 0;
                for (int x = 0; x < w; x += 2)
                {
                    // 4 pixels of the 2x2 block
                    int b0 = s0[0], g0 = s0[1], r0 = s0[2];
                    int b1 = s0[4], g1 = s0[5], r1 = s0[6];
                    int b2 = s1[0], g2 = s1[1], r2 = s1[2];
                    int b3 = s1[4], g3 = s1[5], r3 = s1[6];
                    y0[x] = ToY(r0, g0, b0);
                    y0[x + 1] = ToY(r1, g1, b1);
                    if (yLast == 1)
                    {
                        y1[x] = ToY(r2, g2, b2);
                        y1[x + 1] = ToY(r3, g3, b3);
                    }
                    // average RGB over block, then one UV pair
                    int r = (r0 + r1 + r2 + r3) >> 2;
                    int g = (g0 + g1 + g2 + g3) >> 2;
                    int b = (b0 + b1 + b2 + b3) >> 2;
                    uv[x] = ToU(r, g, b);
                    uv[x + 1] = ToV(r, g, b);
                    s0 += 8;
                    s1 += 8;
                }
            }
        }
    }

    private static byte ToY(int r, int g, int b)
    {
        int v = 16 + ((66 * r + 129 * g + 25 * b + 128) >> 8);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private static byte ToU(int r, int g, int b)
    {
        int v = 128 + ((-38 * r - 74 * g + 112 * b + 128) >> 8);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private static byte ToV(int r, int g, int b)
    {
        int v = 128 + ((112 * r - 94 * g - 18 * b + 128) >> 8);
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    public void Dispose() { _acc?.Dispose(); _mmf?.Dispose(); _ready = false; }
}

// Own MF virtual camera registration. Falls back to OBS path if DLL missing.
public static class EPhoneCam
{
    [DllImport("EPhoneCam", CallingConvention = CallingConvention.StdCall)]
    private static extern int EPhoneCam_Register();
    [DllImport("EPhoneCam", CallingConvention = CallingConvention.StdCall)]
    private static extern int EPhoneCam_Unregister();
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    public static bool Available { get; private set; }

    public static bool TryStart(out string msg)
    {
        // DLL beside exe, else fallback.
        var dll = Path.Combine(AppContext.BaseDirectory, "EPhoneCam.dll");
        if (!File.Exists(dll)) { msg = "own cam DLL missing, use OBS fallback"; return false; }
        try
        {
            if (LoadLibrary(dll) == IntPtr.Zero) { msg = "LoadLibrary fail"; return false; }
            int hr = EPhoneCam_Register();
            Available = hr == 0;
            msg = hr == 0 ? "ephone-cam: live as ePhone Camera" : $"cam hr=0x{hr:X8}";
            return Available;
        }
        catch (Exception e) { msg = "cam: " + e.Message; return false; }
    }
}

// Own mic driver feeder. Writes 48kHz mono to \\.\EPhoneMic.
// Phone gives 16k mono -> x3 upsample here. Falls back to MicOut/VB-CABLE if no driver.
public sealed class EPhoneMicFeed : IDisposable
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string f, uint acc, uint share, IntPtr sec,
        uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] b, uint n, out uint w, IntPtr o);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);

    private IntPtr _h = new(-1);
    public bool Ready => _h != new IntPtr(-1);

    public bool Open()
    {
        _h = CreateFile(@"\\.\\EPhoneMic", 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return Ready;
    }

    // 16k mono in -> 48k mono out (repeat each sample x3, cheap, no alias filter needed for voice v0).
    public void Write16k(short[] pcm16k)
    {
        if (!Ready) return;
        var up = new short[pcm16k.Length * 3];
        for (int i = 0; i < pcm16k.Length; i++) up[i * 3] = up[i * 3 + 1] = up[i * 3 + 2] = pcm16k[i];
        var bytes = new byte[up.Length * 2];
        Buffer.BlockCopy(up, 0, bytes, 0, bytes.Length);
        WriteFile(_h, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
    }

    public void Dispose() { if (Ready) CloseHandle(_h); _h = new IntPtr(-1); }
}
