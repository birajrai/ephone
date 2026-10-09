using Newtonsoft.Json.Linq;
using OBSWebsocketDotNet;

namespace EPhone.Engine;

// Routes phone MJPEG into OBS, starts OBS Virtual Camera.
// Needs OBS running + WebSocket server on (Tools -> WebSocket Server Settings).
public sealed class ObsVcam : IDisposable
{
    private readonly OBSWebsocket _obs = new();
    private const string InputName = "ePhone";

    public bool Connected => _obs.IsConnected;

    public void Connect(string url = "ws://127.0.0.1:4455", string password = "")
    {
        if (_obs.IsConnected) return;
        _obs.ConnectAsync(url, password);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!_obs.IsConnected && DateTime.UtcNow < until) Thread.Sleep(100);
        if (!_obs.IsConnected) throw new TimeoutException("OBS not answering. Open OBS + enable WebSocket server.");
    }

    public void ShowPhone(string phoneIp)
    {
        var names = new HashSet<string>();
        try
        {
            foreach (var i in _obs.GetInputList()) names.Add(i.InputName);
        }
        catch { }
        var settings = new JObject { ["input"] = $"http://{phoneIp}:8081/video" };
        if (names.Contains(InputName))
        {
            _obs.SetInputSettings(InputName, settings, false);
        }
        else
        {
            string scene;
            try { scene = _obs.GetCurrentProgramScene(); }
            catch { scene = _obs.GetSceneList().Scenes[0].Name; }
            _obs.CreateInput(scene, InputName, "ffmpeg_source", settings, true);
        }
        _obs.StartVirtualCam();
    }

    public void Stop()
    {
        try { _obs.StopVirtualCam(); } catch { }
    }

    public void Dispose()
    {
        try { _obs.Disconnect(); } catch { }
    }
}
