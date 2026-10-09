# ePhone drivers — own fake cam + mic (WO-style)

## Arch
```
Phone (MJPEG :8081 / PCM :8082)
  -> cs/EPhone Engine (Phone.cs)
  -> FrameServer (shared mem + named pipe)
  -> EPhoneCam (MF virtual camera, user-mode, no signing)
  -> EPhoneMic (SysVAD fork, kernel, needs signing)
  -> Zoom/Meet/OBS see "ePhone Camera" + "ePhone Mic"
```

## Status
- `cam/` = MF virtual camera skeleton. Builds with VS2022 + WinSDK 10. No driver signing.
- `mic/` = SysVAD-fork skeleton. Needs WDK + EV cert + attestation. Dev = testsigning.
- `cs` side `Engine/EPhoneDevices.cs` feeds both, falls back to OBS/VB-CABLE until drivers installed.

## Bring-up
1. Cam: `cam/register.ps1` (admin). Check `ffmpeg -list_devices true -f dshow -i dummy` or OBS -> ePhone Camera.
2. Mic: enable testsigning, `mic/install.ps1` (admin), reboot, feed via app. Release = EV cert + Partner Center attestation.
3. App: Start Camera/Mic in cs/EPhone. Log shows `ephone-cam: live` / `ephone-mic: live`, else fallback path.

## Why split
- Cam: MF virtual camera is user-mode COM. No kernel. Win10 2004+ only.
- Mic: Windows has no user-mode virtual-mic API. Must be audio driver (SYSVAD). Signing mandatory on x64 + SecureBoot.
