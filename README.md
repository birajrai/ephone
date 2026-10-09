# ePhone — phone as PC cam + mic

No FX. Pure passthrough. Android + Windows + WiFi local IP.

## Layout
- `android/` — Kotlin sender (CameraX + AudioRecord, MJPEG/PCM, res/fps selectable)
- `cs/` — desktop app (Avalonia, Win+Linux: preview + route to vcam / virtual mic)
- `driver/` — Windows-only helpers (own MF virtual camera + virtual mic)
- `protocol.md` — wire spec

## Run
1. Install OBS + enable Virtual Camera, install VB-CABLE (Windows fallback path).
2. `dotnet run --project cs/EPhone` — pick phone IP, res (640x480/1280x720/1920x1080), fps (15/20/30).
3. On phone: Start Camera + Start Mic. PC previews + routes to virtual devices.

## Stack
- Phone: Kotlin, CameraX, AudioRecord, MediaCodec H264, Opus, google-webrtc, QR pair
- PC: C# Avalonia .NET 8 (`cs/EPhone`), PortAudio, SkiaSharp, OBS-WebSocket
- Net: `ws://phone-ip:8080`, UDP media, TCP fallback. Manual IP first, mDNS later.

## MVP
- [ ] 720p video + mic audio to PC over WiFi
- [ ] PC preview window
- [ ] Route to OBS vcam + VB-CABLE (Zoom/Meet-ready)
- [ ] Mute, flip, reconnect
Out: effects, multi-cam, remote internet, iOS, login.
