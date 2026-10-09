using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace EPhone.Engine;

// Shared-mem video feeder for driver/cam/EPhoneCam.dll.
// Layout: [CamHeader 24B][RGB32 BGRA bottom-up, up to 1920x1080].
// Passthrough: phone pixels bit-exact, zero color conversion. Writer = this.
public sealed class FrameServer : IDisposable
{
    public static readonly (int W, int H)[] Modes = [(640, 480), (1280, 720), (1920, 1080)];
    public static readonly int[] FpsModes = [15, 20, 30];
    public const string MapName = "Local\\EPhoneCam";
    private const int HeaderBytes = 24;
    private const int MaxW = 1920, MaxH = 1080;
    private const long MaxBytes = 1920L * 1080 * 4;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private ulong _frameId;
    private long _lastPushMs;
    private bool _ready;

    public int Fps { get; }
    public bool Throttle { get; }
    public bool Ready => _ready;
    public int LastW { get; private set; }
    public int LastH { get; private set; }

    // fps <= 0 + throttle:false = original phone fps: every frame passes, header carries phone rate.
    public FrameServer(int fps = 30, bool throttle = true)
    {
        Fps = fps <= 0 ? 30 : Math.Clamp(fps, 5, 60);
        Throttle = throttle && fps > 0;
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

    // Phone gives MJPEG JPEGs. Decode -> shared mem untouched (native size, native color).
    // Shrink only if over driver slot. Throttle only when a fixed fps is selected.
    public unsafe void PushJpeg(byte[] jpg)
    {
        if (!_ready || _acc is null) return;
        long now = Environment.TickCount64;
        if (Throttle && now - _lastPushMs < 1000 / Fps) return; // drop to target fps
        try
        {
            using var bmp = SKBitmap.Decode(jpg);
            if (bmp is null) return;
            // Native phone quality: no forced resize. Shrink only if over driver slot.
            using var down = (bmp.Width > MaxW || bmp.Height > MaxH)
                ? FitInside(bmp, MaxW, MaxH) : null;
            var src = down ?? bmp;
            int w = src.Width;
            int h = src.Height;
            if (w < 320 || h < 240 || (long)w * h * 4 > MaxBytes) return;
            // BGRA pixels via SKImage.ReadPixels (top-down), then flip to bottom-up DIB.
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = new byte[w * h * 4];
            using var img = SKImage.FromBitmap(src);
            fixed (byte* p = pixels)
            {
                if (!img.ReadPixels(info, (IntPtr)p, w * 4)) return;
            }
            FlipVertical(pixels, w, h);
            _frameId++;
            _lastPushMs = now;
            LastW = w; LastH = h;
            _acc.Write(0, (uint)w);
            _acc.Write(4, (uint)h);
            _acc.Write(8, (uint)Fps);
            _acc.Write(12, 2u); // 2 = RGB32 BGRA bottom-up (1 = legacy NV12)
            _acc.Write(16, _frameId);
            _acc.WriteArray(HeaderBytes, pixels, 0, pixels.Length);
        }
        catch { }
    }

    private static void FlipVertical(byte[] px, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            int a = y * stride, b = (h - 1 - y) * stride;
            Buffer.BlockCopy(px, a, tmp, 0, stride);
            Buffer.BlockCopy(px, b, px, a, stride);
            Buffer.BlockCopy(tmp, 0, px, b, stride);
        }
    }

    private static SKBitmap? FitInside(SKBitmap bmp, int maxW, int maxH)
    {
        float s = Math.Min(maxW / (float)bmp.Width, maxH / (float)bmp.Height);
        int w = Math.Max(2, ((int)(bmp.Width * s)) & ~1);
        int h = Math.Max(2, ((int)(bmp.Height * s)) & ~1);
        return bmp.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
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
