// CameraPipe.kt — CameraX frames -> NV21 -> MjpegServer.
package com.ephone.sender

import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import java.nio.ByteBuffer

class CameraPipe(private val mjpeg: MjpegServer) : ImageAnalysis.Analyzer {
    private var last = 0L
    override fun analyze(img: ImageProxy) {
        try {
            val now = System.currentTimeMillis()
            val minGap = 1000L / mjpeg.fps.coerceIn(5, 60)
            if (now - last < minGap) return
            last = now
            val nv21 = yuv420ToNv21(img)
            if (nv21 != null) mjpeg.pushYuv(nv21, img.width, img.height)
        } catch (_: Exception) {
        } finally { img.close() }
    }
    // Stride-safe YUV_420_888 -> NV21. Old code assumed pixelStride 1, broke on many devices.
    private fun yuv420ToNv21(img: ImageProxy): ByteArray? {
        if (img.format != android.graphics.ImageFormat.YUV_420_888) return null
        val w = img.width; val h = img.height
        val yPlane = img.planes[0]; val uPlane = img.planes[1]; val vPlane = img.planes[2]
        val out = ByteArray(w * h * 3 / 2)
        // Y row by row (rowStride may exceed w)
        val yBuf = yPlane.buffer
        val yRowStride = yPlane.rowStride
        var o = 0
        val yTmp = ByteArray(yRowStride)
        for (row in 0 until h) {
            yBuf.position(row * yRowStride)
            val n = minOf(w, yBuf.remaining())
            yBuf.get(out, o, n)
            o += n
        }
        // UV: NV21 = V then U, sampled every 2x2. Honor pixelStride/rowStride.
        val uBuf = uPlane.buffer; val vBuf = vPlane.buffer
        val uRowStride = uPlane.rowStride; val vRowStride = vPlane.rowStride
        val uPixStride = uPlane.pixelStride; val vPixStride = vPlane.pixelStride
        var oo = w * h
        for (row in 0 until h / 2) {
            for (col in 0 until w / 2) {
                val vu = vBuf.get(row * vRowStride + col * vPixStride)
                val uu = uBuf.get(row * uRowStride + col * uPixStride)
                out[oo++] = vu
                out[oo++] = uu
            }
        }
        return out
    }
}
