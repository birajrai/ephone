# Wire spec v1 (local WiFi only)

## Signaling (TCP, JSON lines, port 8080)
Phone = server. PC connects to `ws://<phone-ip>:8080` (v1: plain TCP + JSON, upgrade to WS later).

- `hello` PC→phone: `{type, client, want:[video,audio]}`
- `offer` phone→PC: SDP (WebRTC pro) — v1 simple: `{type:stream, video:mjpeg, audio:opus, width, height, fps}`
- `bye` either way.

## Media v1 (simple, debuggable)
- Video: MJPEG over HTTP `http://<phone-ip>:8081/video` (phone native, q80 default, PC may request mode). Pro: H264/WebRTC.
- Audio: Opus over TCP port 8082, 48kHz mono, 20ms frames, length-prefixed. Pro: SRTP.

## Pairing
Manual IP type-in first. QR + mDNS (`_ephone._tcp`) later.

## Control
`{type:mute,bool}`, `{type:camera,facing:front|back}`, `{type:ping}` / pong for lag check.
`{type:video,width,height,fps,quality}` PC→phone: request capture mode.
Allowed: 640x480, 1280x720, 1920x1080 @ 5-30fps, q20-90. Phone applies + rebinds camera.
