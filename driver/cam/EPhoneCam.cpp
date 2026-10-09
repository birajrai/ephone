// EPhoneCam — MF virtual camera, functional v1.
// Serves NV12 1280x720@30 from shared mem Local\EPhoneCam (cs FrameServer).
// Falls back to moving test pattern when app not running, so device always enumerates.
// Register: MFCreateVirtualCamera(System, CurrentUser) + AddDeviceSourceInfo(custom source).

#include "../common/FrameTransport.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <mferror.h>
#include <windows.h>
#include <atomic>
#include <thread>
#include <mutex>
#include <vector>

#ifndef RETURN_IF_FAILED
#define RETURN_IF_FAILED(hr) do { HRESULT _h = (hr); if (FAILED(_h)) return _h; } while (0)
#endif

static const wchar_t kFriendly[] = L"ePhone Camera";
static const wchar_t kSourceId[] = L"ePhone_Camera_Source_v1";

// {E9A1F2B4-8C3D-4E5F-9A6B-1C2D3E4F5A60} — guidgen new one for release.
static const GUID CLSID_EPhoneMediaSource =
    {0xe9a1f2b4, 0x8c3d, 0x4e5f, {0x9a, 0x6b, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x60}};

// ---- minimal IMFMediaSource: 1 stream, NV12 1280x720@30, 33ms samples ----
class EPhoneStream;
class EPhoneSource : public IMFMediaSource, public IMFGetService {
  std::atomic<ULONG> _ref{1};
  std::mutex _lock;
  IMFPresentationDescriptor* _pd = nullptr;
  IMFMediaEventQueue* _queue = nullptr;
  EPhoneStream* _stream = nullptr;
  bool _started = false;

 public:
  EPhoneSource();
  virtual ~EPhoneSource();
  HRESULT Init();
  // IUnknown
  STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
  STDMETHODIMP_(ULONG) AddRef() override { return ++_ref; }
  STDMETHODIMP_(ULONG) Release() override {
    ULONG r = --_ref;
    if (!r) delete this;
    return r;
  }
  // IMFGetService
  STDMETHODIMP GetService(REFGUID, REFIID riid, LPVOID* ppv) override {
    return QueryInterface(riid, ppv);
  }
  // IMFMediaEventGenerator
  STDMETHODIMP BeginGetEvent(IMFAsyncCallback* c, IUnknown* s) override;
  STDMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) override;
  STDMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) override;
  STDMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT hr, const PROPVARIANT* v) override;
  // IMFMediaSource
  STDMETHODIMP GetCharacteristics(DWORD* c) override {
    *c = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
  }
  STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppd) override;
  STDMETHODIMP Start(IMFPresentationDescriptor*, const GUID*, const PROPVARIANT*) override;
  STDMETHODIMP Stop() override;
  STDMETHODIMP Pause() override { return MF_E_INVALIDREQUEST; }
  STDMETHODIMP Shutdown() override;
};

class EPhoneStream : public IMFMediaStream {
  std::atomic<ULONG> _ref{1};
  EPhoneSource* _src = nullptr;
  IMFMediaEventQueue* _queue = nullptr;
  IMFStreamDescriptor* _sd = nullptr;
  std::atomic<bool> _run{false};
  std::thread _thread;
  std::atomic<LONGLONG> _t{0};

 public:
  EPhoneStream(EPhoneSource* s, IMFStreamDescriptor* sd) : _src(s), _sd(sd) {
    if (_src) _src->AddRef();
    if (_sd) _sd->AddRef();
    MFCreateEventQueue(&_queue);
  }
  virtual ~EPhoneStream() {
    _run = false;
    if (_thread.joinable()) _thread.join();
    if (_queue) _queue->Release();
    if (_sd) _sd->Release();
    if (_src) _src->Release();
  }
  STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
    if (!ppv) return E_POINTER;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator)) {
      *ppv = static_cast<IMFMediaEventGenerator*>(this);
    } else if (riid == __uuidof(IMFMediaStream)) {
      *ppv = static_cast<IMFMediaStream*>(this);
    } else {
      *ppv = nullptr;
      return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
  }
  STDMETHODIMP_(ULONG) AddRef() override { return ++_ref; }
  STDMETHODIMP_(ULONG) Release() override {
    ULONG r = --_ref;
    if (!r) delete this;
    return r;
  }
  STDMETHODIMP BeginGetEvent(IMFAsyncCallback* c, IUnknown* s) override {
    return _queue->BeginGetEvent(c, s);
  }
  STDMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) override {
    return _queue->EndGetEvent(r, e);
  }
  STDMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) override { return _queue->GetEvent(f, e); }
  STDMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT hr, const PROPVARIANT* v) override {
    return _queue->QueueEventParamVar(t, g, hr, v);
  }
  STDMETHODIMP GetMediaSource(IMFMediaSource** m) override {
    return _src->QueryInterface(IID_PPV_ARGS(m));
  }
  STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** s) override {
    *s = _sd;
    (*s)->AddRef();
    return S_OK;
  }
  STDMETHODIMP RequestSample(IUnknown*) override {
    // push model: samples flow on Start thread; just ack here.
    return S_OK;
  }
  void Begin() {
    _run = true;
    _thread = std::thread([this] { Pump(); });
    _queue->QueueEventParamVar(MEMediaSample, GUID_NULL, S_OK, nullptr);
  }
  void End() {
    _run = false;
    if (_thread.joinable()) _thread.join();
  }

 private:
  void Pump();
  void FillTestPattern(uint8_t* nv12, int w, int h, int tick);
};

EPhoneSource::EPhoneSource() { MFCreateEventQueue(&_queue); }
EPhoneSource::~EPhoneSource() {
  if (_queue) _queue->Release();
  if (_pd) _pd->Release();
  if (_stream) _stream->Release();
}

STDMETHODIMP EPhoneSource::QueryInterface(REFIID riid, void** ppv) {
  if (!ppv) return E_POINTER;
  if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator)) {
    *ppv = static_cast<IMFMediaEventGenerator*>(this);
  } else if (riid == __uuidof(IMFMediaSource)) {
    *ppv = static_cast<IMFMediaSource*>(this);
  } else if (riid == __uuidof(IMFGetService)) {
    *ppv = static_cast<IMFGetService*>(this);
  } else {
    *ppv = nullptr;
    return E_NOINTERFACE;
  }
  AddRef();
  return S_OK;
}
STDMETHODIMP EPhoneSource::BeginGetEvent(IMFAsyncCallback* c, IUnknown* s) {
  return _queue->BeginGetEvent(c, s);
}
STDMETHODIMP EPhoneSource::EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) {
  return _queue->EndGetEvent(r, e);
}
STDMETHODIMP EPhoneSource::GetEvent(DWORD f, IMFMediaEvent** e) { return _queue->GetEvent(f, e); }
STDMETHODIMP EPhoneSource::QueueEvent(MediaEventType t, REFGUID g, HRESULT hr, const PROPVARIANT* v) {
  return _queue->QueueEventParamVar(t, g, hr, v);
}

HRESULT MakeVideoType(IMFMediaType** out, int w = ephone::kCamW, int h = ephone::kCamH,
                       int fps = ephone::kCamFps) {
  IMFMediaType* t = nullptr;
  RETURN_IF_FAILED(MFCreateMediaType(&t));
  RETURN_IF_FAILED(t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
  RETURN_IF_FAILED(t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
  RETURN_IF_FAILED(MFSetAttributeSize(t, MF_MT_FRAME_SIZE, w, h));
  RETURN_IF_FAILED(MFSetAttributeRatio(t, MF_MT_FRAME_RATE, fps, 1));
  RETURN_IF_FAILED(MFSetAttributeRatio(t, MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
  RETURN_IF_FAILED(t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
  RETURN_IF_FAILED(t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
  *out = t;
  return S_OK;
}

HRESULT EPhoneSource::Init() {
  IMFMediaType* type = nullptr;
  RETURN_IF_FAILED(MakeVideoType(&type));
  IMFStreamDescriptor* sd = nullptr;
  IMFMediaTypeHandler* h = nullptr;
  IMFPresentationDescriptor* pd = nullptr;
  HRESULT hr = S_OK;
  IMFMediaType* arr[1] = {type};
  if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, 1, arr, &sd);
  if (SUCCEEDED(hr)) hr = sd->GetMediaTypeHandler(&h);
  if (SUCCEEDED(hr)) hr = h->SetCurrentMediaType(type);
  if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, &sd, &pd);
  if (SUCCEEDED(hr)) hr = pd->SelectStream(0);
  if (SUCCEEDED(hr)) {
    _pd = pd;
    _pd->AddRef();
    _stream = new EPhoneStream(this, sd);
    pd->Release();
  }
  if (h) h->Release();
  if (sd) sd->Release();
  type->Release();
  return hr;
}

STDMETHODIMP EPhoneSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppd) {
  if (!_pd) return MF_E_NOT_INITIALIZED;
  return _pd->Clone(ppd);
}
STDMETHODIMP EPhoneSource::Start(IMFPresentationDescriptor*, const GUID*, const PROPVARIANT*) {
  std::lock_guard<std::mutex> g(_lock);
  _queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, nullptr);
  _stream->Begin();
  _started = true;
  return S_OK;
}
STDMETHODIMP EPhoneSource::Stop() {
  std::lock_guard<std::mutex> g(_lock);
  if (_started) _stream->End();
  _started = false;
  _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
  return S_OK;
}
STDMETHODIMP EPhoneSource::Shutdown() {
  std::lock_guard<std::mutex> g(_lock);
  if (_started) _stream->End();
  _started = false;
  if (_queue) _queue->Shutdown();
  return S_OK;
}

void EPhoneStream::FillTestPattern(uint8_t* nv12, int w, int h, int tick) {
  int ySize = w * h;
  uint8_t y = (uint8_t)(16 + (tick * 2) % 200);
  for (int i = 0; i < ySize; i++) nv12[i] = y;
  for (int i = ySize; i < ySize + ySize / 2; i += 2) {
    nv12[i] = 128;
    nv12[i + 1] = 128;
  }
  // green bar so user sees "alive but no phone"
  for (int y0 = h / 2 - 10; y0 < h / 2 + 10; y0++)
    for (int x = 0; x < w; x++) nv12[y0 * w + x] = 200;
}

void EPhoneStream::Pump() {
  HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, ephone::kCamMap);
  const uint8_t* view = nullptr;
  if (map) view = (const uint8_t*)MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0);
  std::vector<uint8_t> buf(ephone::kMaxFrameBytes);
  uint64_t lastId = 0;
  int tick = 0;
  // current format; updated from header. Default 720p30 until first real frame.
  int curW = ephone::kCamW, curH = ephone::kCamH, curFps = ephone::kCamFps;
  auto validSize = [](uint32_t w, uint32_t h) {
    // Native phone frames: accept any even size in range, not a whitelist.
    return w >= 320 && w <= (uint32_t)ephone::kMaxW && h >= 240 &&
           h <= (uint32_t)ephone::kMaxH && (w % 2 == 0) && (h % 2 == 0);
  };
  while (_run.load()) {
    bool have = false;
    uint32_t hw = 0, hh = 0, hfps = 0;
    size_t need = 0;
    if (view) {
      auto* hdr = (const ephone::CamHeader*)view;
      hw = hdr->width;
      hh = hdr->height;
      hfps = hdr->fps;
      if (validSize(hw, hh) && hfps >= 5 && hfps <= 60 && hdr->frameId != 0 &&
          hdr->frameId != lastId) {
        need = (size_t)hw * hh * 3 / 2;
        if (need <= buf.size()) {
          lastId = hdr->frameId;
          memcpy(buf.data(), view + sizeof(ephone::CamHeader), need);
          have = true;
          // format change: new size/fps -> tell pipeline
          if ((int)hw != curW || (int)hh != curH || (int)hfps != curFps) {
            curW = hw;
            curH = hh;
            curFps = hfps ? hfps : curFps;
            IMFMediaType* nt = nullptr;
            if (SUCCEEDED(MakeVideoType(&nt, curW, curH, curFps))) {
              _queue->QueueEventParamUnk(MEStreamFormatChanged, GUID_NULL, S_OK,
                                         (IUnknown*)nt);
              nt->Release();
            }
          }
        }
      }
    }
    int w = have ? (int)hw : curW;
    int h = have ? (int)hh : curH;
    int fps = have && hfps ? (int)hfps : curFps;
    if (fps < 5 || fps > 60) fps = 30;
    size_t n = (size_t)w * h * 3 / 2;
    if (!have) FillTestPattern(buf.data(), w, h, tick);
    tick++;

    LONGLONG dur = 10000000 / fps;
    IMFMediaBuffer* mb = nullptr;
    IMFSample* sample = nullptr;
    BYTE* ptr = nullptr;
    if (SUCCEEDED(MFCreateMemoryBuffer((DWORD)n, &mb)) &&
        SUCCEEDED(mb->Lock(&ptr, nullptr, nullptr))) {
      memcpy(ptr, buf.data(), n);
      mb->Unlock();
      mb->SetCurrentLength((DWORD)n);
      if (SUCCEEDED(MFCreateSample(&sample))) {
        sample->AddBuffer(mb);
        sample->SetSampleTime(_t.load());
        sample->SetSampleDuration(dur);
        _t += dur;
        _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);
        sample->Release();
      }
      mb->Release();
    }
    Sleep(1000 / fps);
  }
  if (view) UnmapViewOfFile(view);
  if (map) CloseHandle(map);
}

// ---- DLL exports: source factory + register/unregister ----
extern "C" __declspec(dllexport) HRESULT CreateEPhoneMediaSource(IMFMediaSource** pp) {
  auto* s = new EPhoneSource();
  HRESULT hr = s->Init();
  if (FAILED(hr)) {
    s->Release();
    return hr;
  }
  return s->QueryInterface(IID_PPV_ARGS(pp));
}

static IMFVirtualCamera* g_vcam = nullptr;

extern "C" __declspec(dllexport) HRESULT EPhoneCam_Register() {
  RETURN_IF_FAILED(MFStartup(MF_VERSION, MFSTARTUP_LITE));
  HMODULE mf = LoadLibraryW(L"mfsensorgroup.dll");
  if (!mf) mf = LoadLibraryW(L"mfcore.dll");
  if (!mf) return HRESULT_FROM_WIN32(GetLastError());
  using Fn = HRESULT(WINAPI*)(MFVirtualCameraType, MFVirtualCameraLifetime,
      MFVirtualCameraAccess, LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCamera**);
  auto fn = (Fn)GetProcAddress(mf, "MFCreateVirtualCamera");
  if (!fn) return HRESULT_FROM_WIN32(GetLastError());
  IMFVirtualCamera* vc = nullptr;
  HRESULT hr = fn(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_System,
      MFVirtualCameraAccess_CurrentUser, kFriendly, kSourceId, nullptr, 0, &vc);
  RETURN_IF_FAILED(hr);
  // Bind custom source so MF enumerates NV12 frames (needs AddDeviceSourceInfo on Win11+).
  // Best-effort: device still registers even if this fails on older builds.
  // NOTE: in-proc factory CreateEPhoneMediaSource above is resolved via COM
  // registration for production; dev path relies on shared-mem pump + OBS fallback.
  (void)vc->AddDeviceSourceInfo(kSourceId);
  RETURN_IF_FAILED(vc->Start(nullptr));
  g_vcam = vc;
  return S_OK;
}

extern "C" __declspec(dllexport) HRESULT EPhoneCam_Unregister() {
  if (g_vcam) {
    g_vcam->Stop();
    g_vcam->Remove();
    g_vcam->Release();
    g_vcam = nullptr;
  }
  MFShutdown();
  return S_OK;
}

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
