// SignalServer.kt — plain TCP JSON lines on :8080.
package com.ephone.sender

import org.json.JSONObject
import java.net.ServerSocket
import kotlin.concurrent.thread

class SignalServer(
    private val port: Int = 8080,
    private val onMute: (Boolean) -> Unit,
    private val onCamera: (String) -> Unit
) {
    @Volatile private var running = false
    fun start() {
        running = true
        thread(isDaemon = true, name = "signal") {
            try {
                ServerSocket(port).use { srv ->
                    while (running) {
                        val c = srv.accept()
                        thread(isDaemon = true) { handle(c) }
                    }
                }
            } catch (_: Exception) {}
        }
    }
    fun stop() { running = false }
    private fun handle(c: java.net.Socket) {
        c.use { s ->
            val r = s.getInputStream().bufferedReader()
            val w = s.getOutputStream().bufferedWriter()
            // hello
            val hello = JSONObject(mapOf("type" to "stream", "video" to "mjpeg",
                "audio" to "pcm16", "width" to 1280, "height" to 720, "fps" to 30))
            w.write(hello.toString() + "\n"); w.flush()
            for (line in r.lines()) {
                try {
                    val j = JSONObject(line)
                    when (j.optString("type")) {
                        "mute" -> onMute(j.optBoolean("muted"))
                        "camera" -> onCamera(j.optString("facing", "back"))
                        "ping" -> { w.write(JSONObject(mapOf("type" to "pong")).toString() + "\n"); w.flush() }
                    }
                } catch (_: Exception) {}
                if (!running) break
            }
        }
    }
}
