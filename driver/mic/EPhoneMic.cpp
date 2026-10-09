// EPhoneMic — WDM virtual microphone, SysVAD-style capture filter.
// Target: 1 capture pin, 48000Hz 16-bit mono (matches WO + cs EPhoneMicFeed).
// Feeder: cs app opens \\.\EPhoneMic, WriteFile raw s16le chunks.
// Driver keeps 2s ring buffer; KS pin drains; underrun -> silence.
//
// Build: WDK 10.0.26100 + VS2022, x64 Release. This file compiles under WDK
// (ntddk.h/portcls.h). NOT compiled by plain WinSDK — by design (kernel only).
// Install dev: testsigning + pnputil. Release: EV + attestation -> .cat.
//
// Topology: 1 filter, 1 capture pin (KSPIN_DATAFLOW_OUT), KSCATEGORY_AUDIO,
// KSCATEGORY_CAPTURE. Format: WAVE_FORMAT_PCM 48k/mono/16-bit.
// Ring: 192000 samples (4s) to survive WiFi jitter. Lock: spinlock + DPC.

#ifdef _KERNEL_MODE
#include <ntddk.h>
#include <portcls.h>
#include <ks.h>
#include <ksmedia.h>

#define EPHONE_POOL_TAG 'hPeM'
#define EPHONE_SAMPLE_RATE 48000
#define EPHONE_CHANNELS 1
#define EPHONE_BITS 16
// 20ms ingest chunks from user-mode (960 samples), 10ms KS packets out (480).
#define EPHONE_INGEST_SAMPLES 960
#define EPHONE_KS_SAMPLES 480
#define EPHONE_RING_SAMPLES (EPHONE_SAMPLE_RATE * 4)

#define IOCTL_EPHONE_WRITE_PCM CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_IN_DIRECT, FILE_WRITE_DATA)

struct EPHONE_RING {
  short buf[EPHONE_RING_SAMPLES];
  volatile LONG writePos;
  volatile LONG readPos;
  KSPIN_LOCK lock;
};

static EPHONE_RING g_ring;

// Ingest: called at DISPATCH_LEVEL from Write dispatch. Drops oldest on overflow.
static void EPhoneRingWrite(const short* pcm, ULONG samples) {
  KIRQL old;
  KeAcquireSpinLock(&g_ring.lock, &old);
  for (ULONG i = 0; i < samples; i++) {
    g_ring.buf[g_ring.writePos] = pcm[i];
    g_ring.writePos = (g_ring.writePos + 1) % EPHONE_RING_SAMPLES;
    if (g_ring.writePos == g_ring.readPos)
      g_ring.readPos = (g_ring.readPos + 1) % EPHONE_RING_SAMPLES;  // overrun: drop oldest
  }
  KeReleaseSpinLock(&g_ring.lock, old);
}

// Drain: called by PortCls capture ISR/DPC to fill KS packet. Silence on underrun.
static void EPhoneRingRead(short* out, ULONG samples) {
  KIRQL old;
  KeAcquireSpinLock(&g_ring.lock, &old);
  for (ULONG i = 0; i < samples; i++) {
    if (g_ring.readPos != g_ring.writePos) {
      out[i] = g_ring.buf[g_ring.readPos];
      g_ring.readPos = (g_ring.readPos + 1) % EPHONE_RING_SAMPLES;
    } else {
      out[i] = 0;
    }
  }
  KeReleaseSpinLock(&g_ring.lock, old);
}

// Device I/O: WriteFile from cs EPhoneMicFeed lands here.
_Use_decl_annotations_ NTSTATUS EPhoneEvtWrite(WDFQUEUE q, WDFREQUEST req, size_t len) {
  UNREFERENCED_PARAMETER(q);
  if (len == 0 || (len % sizeof(short)) != 0) {
    WdfRequestComplete(req, STATUS_INVALID_BUFFER_SIZE);
    return STATUS_INVALID_BUFFER_SIZE;
  }
  PVOID src = nullptr;
  size_t srcLen = 0;
  NTSTATUS st = WdfRequestRetrieveInputBuffer(req, len, &src, &srcLen);
  if (!NT_SUCCESS(st)) {
    WdfRequestComplete(req, st);
    return st;
  }
  // Clamp single write to ring size; feeder already sends 20ms (1920B).
  ULONG samples = (ULONG)(srcLen / sizeof(short));
  if (samples > EPHONE_RING_SAMPLES) samples = EPHONE_RING_SAMPLES;
  EPhoneRingWrite((const short*)src, samples);
  WdfRequestCompleteWithInformation(req, STATUS_SUCCESS, srcLen);
  return STATUS_SUCCESS;
}

// DriverEntry/DeviceAdd/KS descriptors: fork from SysVAD, keep only:
// - Miniport topology with 1 capture pin, KSDATAFORMAT_WAVEFORMATEX 48k/mono/16.
// - FriendlyName "ePhone Mic" (see EPhoneMic.inf).
// Full SysVAD fork steps in driver/README.md. This file holds feeder + ring;
// pin/ISR glue lives in Miniport.cpp (SysVAD-derived, calls EPhoneRingRead).

#else  // !_KERNEL_MODE — user-mode view (IntelliSense / WinSDK): contract only.
#define EPHONE_MIC_DEVICE_PATH L"\\\\.\\EPhoneMic"
#define IOCTL_EPHONE_WRITE_PCM CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_IN_DIRECT, FILE_WRITE_DATA)
// Payload: raw int16 LE mono @48kHz. No header.
#endif
