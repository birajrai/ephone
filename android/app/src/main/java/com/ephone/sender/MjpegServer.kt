// MjpegServer.kt — ServerSocket MJPEG on :8081/video. No extra deps.
package com.ephone.sender

import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import java.io.ByteArrayOutputStream
import java.net.ServerSocket
import java.util.concurrent.ArrayBlockingQueue
import kotlin.concurrent.thread

class MjpegServer(private val port: Int = 8081) {
    private val latest = ArrayBlockingQueue<ByteArray>(2)
    @Volatile private var running = false
    @Volatile var width = 1280
    @Volatile var height = 720
    @Volatile var fps = 20
    @Volatile var quality = 50
    private var srv: ServerSocket? = null

    fun setConfig(w: Int, h: Int, f: Int, q: Int) {
        if (w in listOf(640, 1280, 1920) && h in listOf(480, 720, 1080)) { width = w; height = h }
        fps = f.coerceIn(5, 30)
        quality = q.coerceIn(20, 90)
        latest.clear()
    }

    fun pushYuv(nv21: ByteArray, w: Int, h: Int) {
        // Downscale note: CameraX already targets width x height via setTargetResolution.
        // Encode at requested quality (lower q = less bandwidth for calls).
        val yuv = YuvImage(nv21, ImageFormat.NV21, w, h, null)
        val out = ByteArrayOutputStream(48 * 1024)
        yuv.compressToJpeg(Rect(0, 0, w, h), quality, out)
        val jpg = out.toByteArray()
        if (latest.remainingCapacity() == 0) latest.poll()
        latest.offer(jpg)
    }

    fun start() {
        stop()
        running = true
        thread(isDaemon = true, name = "mjpeg") {
            try {
                ServerSocket(port).use { s ->
                    srv = s
                    while (running) {
                        val c = try { s.accept() } catch (_: Exception) { break }
                        thread(isDaemon = true) { serve(c) }
                    }
                }
            } catch (_: Exception) {}
        }
    }
    fun stop() { running = false; try { srv?.close() } catch (_: Exception) {}; srv = null }

    private fun serve(c: java.net.Socket) {
        c.use { s ->
            // eat request headers
            val r = s.getInputStream().bufferedReader()
            try { while (true) { val l = r.readLine() ?: break; if (l.isEmpty()) break } } catch (_: Exception) {}
            val w = s.getOutputStream()
            val head = "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n"
            w.write(head.toByteArray()); w.flush()
            try {
                while (running && !c.isClosed) {
                    val jpg = latest.take()
                    val h = "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ${jpg.size}\r\n\r\n"
                    w.write(h.toByteArray()); w.write(jpg); w.write("\r\n".toByteArray()); w.flush()
                }
            } catch (_: Exception) {}
        }
    }
}
