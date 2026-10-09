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
    private const long MaxBytes = 1920L * 1080 * 3 / 2;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private ulong _frameId;
    private long _lastPushMs;
    private bool _ready;

    public int W { get; }
    public int H { get; }
    public int Fps { get; }
    public bool Ready => _ready;

    public FrameServer(int w = 1280, int h = 720, int fps = 30)
    {
        W = w; H = h; Fps = Math.Clamp(fps, 5, 60);
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
            using var scaled = bmp.Width == W && bmp.Height == H ? null
                : bmp.Resize(new SKImageInfo(W, H), SKFilterQuality.Medium);
            var src = scaled ?? bmp;
            // BGRA pixels via SKImage.ReadPixels
            var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = new byte[W * H * 4];
            using var img = SKImage.FromBitmap(src);
            fixed (byte* p = pixels)
            {
                if (!img.ReadPixels(info, (IntPtr)p, W * 4)) return;
            }
            // BGRA -> NV12 into stack buffer, then write to MMF
            var nv12 = new byte[W * H * 3 / 2];
            BgraToNv12(pixels, nv12, W, H);
            _frameId++;
            _lastPushMs = now;
            _acc.Write(0, (uint)W);
            _acc.Write(4, (uint)H);
            _acc.Write(8, (uint)Fps);
            _acc.Write(12, (uint)0x3231564E); // 'NV12'
            _acc.Write(16, _frameId);
            _acc.WriteArray(HeaderBytes, nv12, 0, nv12.Length);
        }
        catch { }
    }

    private static void BgraToNv12(byte[] bgra, byte[] nv12, int w, int h)
    {
        int ySize = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int yy = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                nv12[y * w + x] = (byte)Math.Clamp(yy, 0, 255);
            }
        }
        int uvOff = ySize;
        for (int y = 0; y < h / 2; y++)
        {
            for (int x = 0; x < w / 2; x++)
            {
                int sx = x * 2, sy = y * 2;
                int i = (sy * w + sx) * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int u = (int)(-0.169 * r - 0.331 * g + 0.5 * b + 128);
                int v = (int)(0.5 * r - 0.419 * g - 0.081 * b + 128);
                int o = uvOff + y * w + x * 2;
                nv12[o] = (byte)Math.Clamp(u, 0, 255);
                nv12[o + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        }
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
