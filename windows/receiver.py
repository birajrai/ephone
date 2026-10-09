"""ePhone Windows receiver v1 — preview + phone audio + signal."""
import argparse, json, socket, struct, sys, threading, time
import numpy as np

def list_audio(dev_label="Audio devices"):
    try:
        import sounddevice as sd
        print(dev_label + ":")
        print(sd.query_devices())
    except Exception as e:
        print(f"sounddevice missing ({e}). Run: pip install -r requirements.txt")

def fake_loop():
    import cv2
    list_audio()
    print("FAKE mode: test pattern. Q quits. Check VB-CABLE gets tone if -q maps there.")
    w, h, t = 640, 480, 0
    try:
        import sounddevice as sd
        sr = 48000
        def tone():
            ph = np.arange(sr, dtype=np.float32)
            while True:
                yield (0.2 * np.sin(2 * np.pi * 440 * (ph + 0) / sr)).astype(np.float32)
        gen = tone()
        with sd.OutputStream(samplerate=sr, channels=1) as s:
            while True:
                img = np.zeros((h, w, 3), np.uint8)
                cv2.putText(img, f"ePhone FAKE {t}", (40, 240),
                            cv2.FONT_HERSHEY_SIMPLEX, 1.2, (0, 255, 0), 2)
                cv2.imshow("ePhone preview (fake phone)", img)
                s.write(next(gen))
                t += 1
                if cv2.waitKey(33) & 0xFF == ord("q"):
                    break
    except ImportError:
        print("Install reqs first: pip install -r requirements.txt")
        sys.exit(2)
    finally:
        try:
            import cv2
            cv2.destroyAllWindows()
        except Exception:
            pass

def mjpeg_loop(url, vcam=False, preview=True, stop=None):
    import cv2
    print(f"MJPEG mode: pulling {url}. Q quits. vcam={vcam}")
    cap = cv2.VideoCapture(url)
    for _ in range(30):
        if cap.isOpened():
            break
        time.sleep(1)
        cap.open(url)
    if not cap.isOpened():
        print("Cannot open stream after 30s. Phone must serve /video first.")
        return False
    cam = None
    if vcam:
        import pyvirtualcam
    while stop is None or not stop.is_set():
        ok, frame = cap.read()
        if not ok:
            print("Stream dropped. Retrying...")
            time.sleep(1)
            cap.open(url)
            continue
        if vcam:
            h, w = frame.shape[:2]
            if cam is None:
                cam = pyvirtualcam.Camera(w, h, 30, print_fps=True)
                print(f"virtual cam live: {cam.device} @ {w}x{h}")
            cam.send(frame[:, :, ::-1])  # BGR -> RGB
            # keep feeding even if user closes preview; sleep to match ~30fps
            cam.sleep_until_next_frame()
        if preview:
            cv2.imshow("ePhone preview (phone)", frame)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break
    cap.release()
    cv2.destroyAllWindows()

def signal_hello(host, port=8080):
    try:
        s = socket.create_connection((host, port), timeout=5)
        f = s.makefile("r")
        print("signal:", f.readline().strip())
        s.close()
    except Exception as e:
        print(f"signal fail: {e}")

def pcm_loop(host, port=8082, device=None, stop=None):
    import sounddevice as sd
    print(f"audio: {host}:{port} -> device={device} (set VB-CABLE index to feed Zoom). Ctrl+C stops.")
    s = socket.create_connection((host, port), timeout=5)
    def recvall(n):
        b = b""
        while len(b) < n:
            c = s.recv(n - len(b))
            if not c: raise ConnectionError("phone closed")
            b += c
        return b
    with sd.OutputStream(samplerate=16000, channels=1, dtype="int16", device=device) as out:
        while stop is None or not stop.is_set():
            (ln,) = struct.unpack(">i", recvall(4))
            out.write(np.frombuffer(recvall(ln), dtype=np.int16))

def phone_loop(url, audio_host, device, vcam=False, preview=True):
    import cv2
    signal_hello(audio_host)
    threading.Thread(target=pcm_loop, args=(audio_host, 8082, device), daemon=True).start()
    mjpeg_loop(url, vcam=vcam, preview=preview)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fake", action="store_true", help="test pattern + tone, no phone needed")
    ap.add_argument("--mjpeg", default="", help="phone MJPEG url, e.g. http://192.168.1.5:8081/video")
    ap.add_argument("--audio", default="", help="phone IP for PCM audio, enables phone_loop with --mjpeg")
    ap.add_argument("--device", default=None, help="sounddevice output index (VB-CABLE) for phone audio")
    ap.add_argument("--signal", default="", help="phone IP to test :8080 hello")
    ap.add_argument("--vcam", action="store_true", help="also send video to OBS Virtual Camera")
    ap.add_argument("--no-preview", action="store_true", help="skip preview window (vcam only)")
    ap.add_argument("--list-audio", action="store_true")
    a = ap.parse_args()
    if a.list_audio:
        list_audio()
        return
    if a.signal:
        signal_hello(a.signal)
        return
    if a.mjpeg and a.audio:
        phone_loop(a.mjpeg, a.audio, int(a.device) if a.device is not None else None,
                   vcam=a.vcam, preview=not a.no_preview)
    elif a.mjpeg:
        mjpeg_loop(a.mjpeg, vcam=a.vcam, preview=not a.no_preview)
    else:
        fake_loop()

if __name__ == "__main__":
    main()
