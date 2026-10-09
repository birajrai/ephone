"""ePhone desktop — tkinter GUI over receiver engine."""
import threading
import tkinter as tk
from tkinter import ttk
import receiver

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("ePhone")
        self.geometry("420x410")
        self.cam_stop = None
        self.mic_stop = None

        ttk.Label(self, text="Phone IP").pack(pady=(10, 0))
        self.ip = ttk.Entry(self)
        self.ip.insert(0, "192.168.254.16")
        self.ip.pack(fill="x", padx=12)

        cam = ttk.LabelFrame(self, text="Camera")
        cam.pack(fill="x", padx=12, pady=8)
        self.vcam = tk.BooleanVar(value=True)
        ttk.Checkbutton(cam, text="Send to OBS Virtual Camera", variable=self.vcam).pack(anchor="w")
        self.cam_btn = ttk.Button(cam, text="Start Camera", command=self.toggle_cam)
        self.cam_btn.pack(pady=4)
        self.cam_state = ttk.Label(cam, text="off")
        self.cam_state.pack()

        mic = ttk.LabelFrame(self, text="Mic")
        mic.pack(fill="x", padx=12, pady=8)
        ttk.Label(mic, text="Output (pick CABLE Input for Zoom/Discord)").pack(anchor="w")
        self.devs = self.audio_devs()
        self.dev = ttk.Combobox(mic, values=[d[0] for d in self.devs], state="readonly")
        self.dev.pack(fill="x")
        for i, d in enumerate(self.devs):
            if "CABLE Input" in d[0]:
                self.dev.current(i)
                break
        self.mic_btn = ttk.Button(mic, text="Start Mic", command=self.toggle_mic)
        self.mic_btn.pack(pady=4)
        self.mic_state = ttk.Label(mic, text="off")
        self.mic_state.pack()

        self.log = tk.Text(self, height=8, state="disabled")
        self.log.pack(fill="both", padx=12, pady=8, expand=True)

    def audio_devs(self):
        try:
            import sounddevice as sd
            return [(f"{i} {d['name']}", i) for i, d in enumerate(sd.query_devices())
                    if d["max_output_channels"] > 0]
        except Exception as e:
            self.say(f"audio err: {e}")
            return []

    def dev_index(self):
        i = self.dev.current()
        return self.devs[i][1] if i >= 0 else None

    def say(self, msg):
        self.log.config(state="normal")
        self.log.insert("end", msg + "\n")
        self.log.see("end")
        self.log.config(state="disabled")

    def url(self):
        return f"http://{self.ip.get().strip()}:8081/video"

    def toggle_cam(self):
        if self.cam_stop is None:
            self.say(receiver.signal_hello(self.ip.get().strip()) or "signal ok")
            self.cam_stop = threading.Event()
            threading.Thread(target=self.cam_run, daemon=True).start()
            self.cam_btn.config(text="Stop Camera")
            self.cam_state.config(text="ON -> OBS Virtual Camera" if self.vcam.get() else "ON preview")
        else:
            self.cam_stop.set()
            self.cam_stop = None
            self.cam_btn.config(text="Start Camera")
            self.cam_state.config(text="off")

    def cam_run(self):
        try:
            ok = receiver.mjpeg_loop(self.url(), vcam=self.vcam.get(),
                                     preview=True, stop=self.cam_stop)
            if ok is False:
                self.say("camera: cannot reach phone. Start Camera on phone first.")
        except Exception as e:
            self.say(f"camera err: {e}")
        finally:
            self.cam_stop = None
            self.cam_btn.config(text="Start Camera")
            self.cam_state.config(text="off")

    def toggle_mic(self):
        if self.mic_stop is None:
            self.mic_stop = threading.Event()
            threading.Thread(target=self.mic_run, daemon=True).start()
            self.mic_btn.config(text="Stop Mic")
            self.mic_state.config(text="ON")
        else:
            self.mic_stop.set()
            self.mic_stop = None
            self.mic_btn.config(text="Start Mic")
            self.mic_state.config(text="off")

    def mic_run(self):
        try:
            receiver.pcm_loop(self.ip.get().strip(), 8082, self.dev_index(), stop=self.mic_stop)
        except Exception as e:
            self.say(f"mic err: {e}")
        finally:
            self.mic_stop = None
            self.mic_btn.config(text="Start Mic")
            self.mic_state.config(text="off")

if __name__ == "__main__":
    App().mainloop()
