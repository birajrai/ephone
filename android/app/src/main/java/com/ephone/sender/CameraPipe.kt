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
            if (now - last < 50) return // ~20fps cap, drops load + bandwidth
            last = now
            val nv21 = yuv420ToNv21(img)
            if (nv21 != null) mjpeg.pushYuv(nv21, img.width, img.height)
        } catch (_: Exception) {
        } finally { img.close() }
    }
    private fun yuv420ToNv21(img: ImageProxy): ByteArray? {
        if (img.format != android.graphics.ImageFormat.YUV_420_888) return null
        val y = img.planes[0].buffer; val u = img.planes[1].buffer; val v = img.planes[2].buffer
        val w = img.width; val h = img.height
        val out = ByteArray(w * h * 3 / 2)
        y.get(out, 0, w * h)
        // VU interleave (NV21). Assumes pixelStride 1 for simplicity; rowStride handled best-effort.
        var o = w * h
        val uvLen = w * h / 4
        val vb = ByteArray(uvLen); val ub = ByteArray(uvLen)
        copyPlane(v, vb); copyPlane(u, ub)
        for (i in 0 until uvLen) { out[o++] = vb[i]; out[o++] = ub[i] }
        return out
    }
    private fun copyPlane(src: ByteBuffer, dst: ByteArray) {
        val p = src.position()
        val n = minOf(src.remaining(), dst.size)
        src.get(dst, 0, n)
        src.position(p)
    }
}
