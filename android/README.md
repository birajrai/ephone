# android/ — Kotlin sender

Needs Android Studio. minSdk 26.

## Perms (AndroidManifest)
CAMERA, RECORD_AUDIO, INTERNET, ACCESS_WIFI_STATE.

## Deps (app/build.gradle)
- camerax-core, camerax-camera2, camerax-lifecycle
- google-webrtc (pro) / MJPEG fallback v1 via NanoHTTPD

## Files
- `MainActivity.kt.stub` — UI: preview + IP show + Start/Stop + flip + mute
- `MjpegServer.kt.stub` — NEXT: serve CameraX frames as MJPEG on :8081/video
- `OpusAudio.kt.stub` — NEXT: AudioRecord 48k mono -> Opus -> TCP :8082
- `SignalServer.kt.stub` — NEXT: JSON hello/stream/mute/camera on :8080

## Bring-up order
1. Preview + Start button (done in stub)
2. MJPEG server, test via `windows/receiver.py --mjpeg http://<phone-ip>:8081/video`
3. Audio TCP, test tone -> VB-CABLE
4. Swap MJPEG/H264 + WebRTC pro
