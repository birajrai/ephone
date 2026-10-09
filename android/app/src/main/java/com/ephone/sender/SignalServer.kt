// SignalServer.kt — plain TCP JSON lines on :8080.
package com.ephone.sender

import org.json.JSONObject
import java.net.ServerSocket
import kotlin.concurrent.thread

class SignalServer(
    private val port: Int = 8080,
    private val onMute: (Boolean) -> Unit,
    private val onCamera: (String) -> Unit,
    private val onVideo: (Int, Int, Int, Int) -> Unit = { _, _, _, _ -> }
) {
    @Volatile private var running = false
    private var srv: ServerSocket? = null
    // Last requested video mode, so hello reports truth instead of hardcoded 720p.
    @Volatile var vw = 1920; @Volatile var vh = 1080; @Volatile var vfps = 30; @Volatile var vq = 80
    fun start() {
        stop()
        running = true
        thread(isDaemon = true, name = "signal") {
            try {
                ServerSocket(port).use { s ->
                    srv = s
                    while (running) {
                        val c = try { s.accept() } catch (_: Exception) { break }
                        thread(isDaemon = true) { handle(c) }
                    }
                }
            } catch (_: Exception) {}
        }
    }
    fun stop() { running = false; try { srv?.close() } catch (_: Exception) {}; srv = null }
    private fun handle(c: java.net.Socket) {
        c.use { s ->
            val r = s.getInputStream().bufferedReader()
            val w = s.getOutputStream().bufferedWriter()
            // hello reports real video mode, not hardcoded values.
            val hello = JSONObject(mapOf("type" to "stream", "video" to "mjpeg",
                "audio" to "pcm16", "width" to vw, "height" to vh, "fps" to vfps))
            w.write(hello.toString() + "\n"); w.flush()
            for (line in r.lines()) {
                try {
                    val j = JSONObject(line)
                    when (j.optString("type")) {
                        "mute" -> onMute(j.optBoolean("muted"))
                        "camera" -> onCamera(j.optString("facing", "back"))
                        "video" -> {
                            vw = j.optInt("width", vw); vh = j.optInt("height", vh)
                            vfps = j.optInt("fps", vfps); vq = j.optInt("quality", vq)
                            onVideo(vw, vh, vfps, vq)
                        }
                        "ping" -> { w.write(JSONObject(mapOf("type" to "pong")).toString() + "\n"); w.flush() }
                    }
                } catch (_: Exception) {}
                if (!running) break
            }
        }
    }
}
