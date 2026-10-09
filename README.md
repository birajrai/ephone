# ePhone — phone as PC cam + mic

No FX. Pure passthrough. Android + Windows + WiFi local IP.

## Layout
- `android/` — Kotlin sender (CameraX + AudioRecord, H264/Opus, WebRTC)
- `windows/` — Python receiver v1 (preview + route to OBS vcam / VB-CABLE)
- `protocol.md` — wire spec

## Run v1 (today, no .NET needed)
1. Install OBS + enable Virtual Camera, install VB-CABLE.
2. `cd windows && pip install -r requirements.txt && python receiver.py --fake`
   Fake mode shows test pattern + tone. Proves preview + audio-out wiring.
3. Real phone path lands next: Android sends MJPEG/H264 to `receiver.py`.

## Stack
- Phone: Kotlin, CameraX, AudioRecord, MediaCodec H264, Opus, google-webrtc, QR pair
- PC: Python 3.13, OpenCV preview, sounddevice out, aiortc later for WebRTC
- Net: `ws://phone-ip:8080`, UDP media, TCP fallback. Manual IP first, mDNS later.

## MVP
- [ ] 720p video + mic audio to PC over WiFi
- [ ] PC preview window
- [ ] Route to OBS vcam + VB-CABLE (Zoom/Meet-ready)
- [ ] Mute, flip, reconnect
Out: effects, multi-cam, remote internet, iOS, login.
