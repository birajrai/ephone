// EPhone shared frame/audio transport.
// Both cs app (writer) and cam DLL / mic driver (readers) use these names.
// v1: fixed 1280x720 NV12 @30fps video, 48kHz mono Int16 audio.

#pragma once
#include <cstdint>

namespace ephone {

constexpr wchar_t kCamMap[] = L"Local\\EPhoneCam";
constexpr wchar_t kCamMutex[] = L"Local\\EPhoneCamMutex";
constexpr wchar_t kMicPipe[] = L"\\\\.\\pipe\\EPhoneMic";

constexpr int kCamW = 1280;
constexpr int kCamH = 720;
constexpr int kCamFps = 30;
// NV12 = Y plane + interleaved UV
constexpr uint32_t kCamFrameBytes = kCamW * kCamH * 3 / 2;

constexpr int kAudioSr = 48000;
constexpr int kAudioCh = 1;
// 20ms chunks: 960 samples
constexpr int kAudioChunkSamples = 960;

struct CamHeader {
  uint32_t width;    // 1280
  uint32_t height;   // 720
  uint32_t fps;      // 30
  uint32_t fourcc;   // 'NV12'
  uint64_t frameId;  // monotonic
  int64_t  qpc;      // QueryPerformanceCounter at write
};
// Layout of kCamMap: [CamHeader][NV12 bytes]

}  // namespace ephone
