using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EPhone.Engine;
using SkiaSharp;

namespace EPhone;

public partial class MainWindow : Window
{
    private Phone.Mjpeg? _mjpeg;
    private Phone.Pcm? _pcm;
    private MicOut? _mic;
    private EPhoneMicFeed? _ownMic;
    private FrameServer? _frames;
    private long _lastPreviewMs;
    private readonly ObsVcam _obs = new();

    public MainWindow()
    {
        InitializeComponent();
        RefreshDevices();
    }

    private string Ip => IpBox.Text?.Trim() ?? "";

    private (int W, int H) SelRes()
    {
        // 0 = Original phone quality: no capture request, PC passes frames through native.
        return ResBox.SelectedIndex switch { 1 => (640, 480), 2 => (1280, 720), 3 => (1920, 1080), _ => (0, 0) };
    }

    private int SelFps()
    {
        return FpsBox.SelectedIndex switch { 0 => 15, 1 => 20, _ => 30 };
    }
    private void Log(string m) => Dispatcher.UIThread.Post(() =>
        LogBox.Text = (LogBox.Text ?? "") + m + "\n");

    private void RefreshDevices()
    {
        try
        {
            var outs = MicOut.Outputs();
            DevBox.ItemsSource = outs.Select(o => o.Name).ToList();
            int cable = outs.FindIndex(o => o.Name.Contains("CABLE Input"));
            DevBox.SelectedIndex = cable >= 0 ? cable : 0;
        }
        catch (Exception e) { Log("audio: " + e.Message); }
    }

    private async void OnCam(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mjpeg is null)
        {
            try
            {
                Log("signal: " + await Phone.HelloAsync(Ip));
                var (vw, vh) = SelRes();
                int vfps = SelFps();
                if (vw > 0) await Phone.SendVideoConfigAsync(Ip, vw, vh, vfps);
                _frames = new FrameServer(vfps);
                _frames.Open();
                _mjpeg = new Phone.Mjpeg();
                _mjpeg.Frame += ShowFrame;
                _mjpeg.Frame += jpg => _frames.PushJpeg(jpg);
                _mjpeg.Start($"http://{Ip}:8081/video");
                if (VcamBox.IsChecked == true)
                {
                    if (EPhoneCam.TryStart(out var ownMsg)) Log(ownMsg);
                    else Log(ownMsg + " — run driver/cam/register.ps1 as admin, then restart camera");
                }
                CamBtn.Content = "Stop Camera";
                var (sw, sh) = SelRes();
                CamState.Text = sw == 0 ? $"ON original@{SelFps()}" : $"ON {sw}x{sh}@{SelFps()}";
            }
            catch (Exception ex) { Log("camera: " + ex.Message); _mjpeg = null; }
        }
        else
        {
            _mjpeg.Dispose(); _mjpeg = null;
            _frames?.Dispose(); _frames = null;
            CamBtn.Content = "Start Camera";
            CamState.Text = "off";
        }
    }

    private void ShowFrame(byte[] jpg)
    {
        // Preview throttled + JPEG re-encode: PNG90 every frame pegged CPU and lagged video.
        long now = Environment.TickCount64;
        if (now - _lastPreviewMs < 66) return;
        _lastPreviewMs = now;
        try
        {
            using var sk = SKBitmap.Decode(jpg);
            if (sk is null) return;
            using var img = SKImage.FromBitmap(sk);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 70);
            var bmp = new Bitmap(data.AsStream());
            Dispatcher.UIThread.Post(() => Preview.Source = bmp);
        }
        catch { }
    }

    private void OnMic(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_pcm is null)
        {
            try
            {
                // Try own driver first, fall back to VB-CABLE.
                _ownMic = new EPhoneMicFeed();
                if (_ownMic.Open())
                {
                    _pcm = new Phone.Pcm();
                    _pcm.Chunk += c => _ownMic.Write16k(c);
                    _pcm.Start(Ip);
                    Log("mic: ePhone Mic driver live");
                    MicBtn.Content = "Stop Mic";
                    MicState.Text = "ON -> ePhone Mic";
                    return;
                }
                _ownMic.Dispose(); _ownMic = null;
                var outs = MicOut.Outputs();
                int sel = DevBox.SelectedIndex;
                if (sel < 0 || sel >= outs.Count) { Log("mic: pick output first"); return; }
                _mic = new MicOut();
                _mic.Open(outs[sel].Index);
                _pcm = new Phone.Pcm();
                _pcm.Chunk += c => _mic.Write(c);
                _pcm.Start(Ip);
                MicBtn.Content = "Stop Mic";
                MicState.Text = "ON -> " + outs[sel].Name;
            }
            catch (Exception ex) { Log("mic: " + ex.Message); _pcm = null; }
        }
        else
        {
            _pcm.Dispose(); _pcm = null;
            _mic?.Dispose(); _mic = null;
            _ownMic?.Dispose(); _ownMic = null;
            MicBtn.Content = "Start Mic";
            MicState.Text = "off";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _mjpeg?.Dispose();
        _pcm?.Dispose();
        _mic?.Dispose();
        _ownMic?.Dispose();
        _frames?.Dispose();
        try { _obs.Stop(); } catch { }
        _obs.Dispose();
        base.OnClosed(e);
    }
}
