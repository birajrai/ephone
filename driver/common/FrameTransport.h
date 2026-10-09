// EPhone shared frame/audio transport.
// Both cs app (writer) and cam DLL / mic driver (readers) use these names.
// Video: dynamic NV12 up to 1920x1080. Header tells real w/h/fps per frame.

#pragma once
#include <cstdint>

namespace ephone {

constexpr wchar_t kCamMap[] = L"Local\\EPhoneCam";
constexpr wchar_t kCamMutex[] = L"Local\\EPhoneCamMutex";
constexpr wchar_t kMicPipe[] = L"\\\\.\\pipe\\EPhoneMic";

constexpr int kCamW = 1280;
constexpr int kCamH = 720;
constexpr int kCamFps = 30;
// NV12 = Y plane + interleaved UV. Max slot fits 1080p; real size in header.
constexpr int kMaxW = 1920;
constexpr int kMaxH = 1080;
constexpr uint32_t kMaxFrameBytes = kMaxW * kMaxH * 3 / 2;
constexpr uint32_t kCamFrameBytes = kCamW * kCamH * 3 / 2;

constexpr int kAudioSr = 48000;
constexpr int kAudioCh = 1;
// 20ms chunks: 960 samples
constexpr int kAudioChunkSamples = 960;

struct CamHeader {
  uint32_t width;    // e.g. 640/1280/1920
  uint32_t height;   // e.g. 480/720/1080
  uint32_t fps;      // e.g. 15/20/30
  uint32_t fourcc;   // 'NV12'
  uint64_t frameId;  // monotonic
  int64_t  qpc;      // QueryPerformanceCounter at write
};
// Layout of kCamMap: [CamHeader][NV12 bytes up to kMaxFrameBytes]
// Valid sizes: 640x480, 1280x720, 1920x1080. Reader must use header w/h, not constants.

}  // namespace ephone
