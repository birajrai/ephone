// EPhone shared frame/audio transport.
// Both cs app (writer) and cam DLL / mic driver (readers) use these names.
// Video: RGB32 BGRA bottom-up passthrough up to 1920x1080. Zero color conversion.

#pragma once
#include <cstdint>

namespace ephone {

constexpr wchar_t kCamMap[] = L"Local\\EPhoneCam";
constexpr wchar_t kCamMutex[] = L"Local\\EPhoneCamMutex";
constexpr wchar_t kMicPipe[] = L"\\\\.\\pipe\\EPhoneMic";

constexpr int kCamW = 1280;
constexpr int kCamH = 720;
constexpr int kCamFps = 30;
// RGB32 = BGRA bottom-up DIB. Max slot fits 1080p; real size in header.
constexpr int kMaxW = 1920;
constexpr int kMaxH = 1080;
constexpr uint32_t kMaxFrameBytes = kMaxW * kMaxH * 4;

constexpr int kAudioSr = 48000;
constexpr int kAudioCh = 1;
// 20ms chunks: 960 samples
constexpr int kAudioChunkSamples = 960;

struct CamHeader {
  uint32_t width;    // native phone width
  uint32_t height;   // native phone height
  uint32_t fps;      // phone rate (or selected cap)
  uint32_t fourcc;   // 2 = RGB32 BGRA bottom-up (1 = legacy NV12)
  uint64_t frameId;  // monotonic
  int64_t  qpc;      // QueryPerformanceCounter at write
};
// Layout of kCamMap: [CamHeader][BGRA bytes up to kMaxFrameBytes]
// Reader must use header w/h, not constants. No color conversion anywhere.

}  // namespace ephone
