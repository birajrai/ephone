// PcmAudio.kt — mic PCM16 16kHz mono -> length-prefixed TCP :8082.
package com.ephone.sender

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import java.net.ServerSocket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread

class PcmAudio(private val port: Int = 8082) {
    @Volatile var muted = false
    @Volatile private var running = false
    private var rec: AudioRecord? = null
    private var srv: ServerSocket? = null

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        stop()
        running = true
        val sr = 16000
        val bs = AudioRecord.getMinBufferSize(sr, AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT) * 2
        if (bs <= 0) { running = false; return false }
        val r = AudioRecord(MediaRecorder.AudioSource.MIC, sr,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bs)
        if (r.state != AudioRecord.STATE_INITIALIZED) { running = false; r.release(); return false }
        rec = r
        rec?.startRecording()
        thread(isDaemon = true, name = "pcm") {
            try {
                ServerSocket(port).use { s ->
                    srv = s
                    while (running) {
                        val c = try { s.accept() } catch (_: Exception) { break }
                        thread(isDaemon = true) { stream(c) }
                    }
                }
            } catch (_: Exception) {}
        }
        return true
    }
    fun stop() { running = false; try { srv?.close() } catch (_: Exception) {}; srv = null; try { rec?.stop(); rec?.release() } catch (_: Exception) {}; rec = null }

    private fun stream(c: java.net.Socket) {
        c.use { s ->
            val out = s.getOutputStream()
            val buf = ShortArray(320) // 20ms @16k
            val hdr = ByteArray(4)
            while (running && !c.isClosed) {
                val n = rec?.read(buf, 0, buf.size) ?: -1
                if (n <= 0) continue
                if (muted) buf.fill(0, 0, n)
                ByteBuffer.wrap(hdr).order(ByteOrder.BIG_ENDIAN).putInt(n * 2)
                try { out.write(hdr); out.write(buf.toByteArray(n)); out.flush() }
                catch (_: Exception) { break }
            }
        }
    }
    private fun ShortArray.toByteArray(n: Int): ByteArray {
        val b = ByteBuffer.allocate(n * 2).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until n) b.putShort(this[i])
        return b.array()
    }
}
