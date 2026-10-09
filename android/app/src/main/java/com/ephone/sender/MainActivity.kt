package com.ephone.sender

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import java.net.Inet4Address
import java.net.NetworkInterface

class MainActivity : AppCompatActivity() {
    private val mjpeg = MjpegServer()
    private val audio = PcmAudio()
    private lateinit var signal: SignalServer
    private var facing = CameraSelector.LENS_FACING_BACK
    private var camOn = false
    private var micOn = false
    private var granted = false
    private lateinit var status: TextView
    private lateinit var camBtn: Button
    private lateinit var micBtn: Button

    private val perms = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()) { m ->
        granted = (m[Manifest.permission.CAMERA] == true &&
                   m[Manifest.permission.RECORD_AUDIO] == true)
        refresh()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        signal = SignalServer(onMute = { audio.muted = it }, onCamera = {
            facing = if (it == "front") CameraSelector.LENS_FACING_FRONT
                     else CameraSelector.LENS_FACING_BACK
            if (camOn) bindCamera()
        })
        signal.start()
        status = TextView(this)
        camBtn = Button(this).apply { setOnClickListener { toggleCam() } }
        micBtn = Button(this).apply { setOnClickListener { toggleMic() } }
        val flip = Button(this).apply {
            text = "Flip"
            setOnClickListener {
                facing = if (facing == CameraSelector.LENS_FACING_BACK)
                    CameraSelector.LENS_FACING_FRONT else CameraSelector.LENS_FACING_BACK
                if (camOn) bindCamera()
            }
        }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(status); addView(camBtn); addView(micBtn); addView(flip)
        })
        perms.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO))
        refresh()
    }

    private fun toggleCam() {
        if (!granted) { perms.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)); return }
        camOn = !camOn
        if (camOn) { mjpeg.start(); bindCamera() } else { mjpeg.stop(); unbindCamera() }
        refresh()
    }

    private fun toggleMic() {
        if (!granted) { perms.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)); return }
        if (!micOn) {
            if (!audio.start()) { status.text = "Mic failed (in use?). " + base(); return }
        } else audio.stop()
        micOn = !micOn
        refresh()
    }

    private fun bindCamera() {
        val fut = ProcessCameraProvider.getInstance(this)
        fut.addListener({
            val prov = fut.get()
            prov.unbindAll()
            val sel = CameraSelector.Builder().requireLensFacing(facing).build()
            val analysis = ImageAnalysis.Builder()
                .setTargetResolution(android.util.Size(1280, 720))
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
            analysis.setAnalyzer(ContextCompat.getMainExecutor(this), CameraPipe(mjpeg))
            try { prov.bindToLifecycle(this, sel, analysis) }
            catch (e: Exception) { camOn = false; mjpeg.stop(); status.text = "Cam busy: " + e.message }
            refresh()
        }, ContextCompat.getMainExecutor(this))
    }

    private fun unbindCamera() {
        try { ProcessCameraProvider.getInstance(this).get().unbindAll() } catch (_: Exception) {}
    }

    private fun base() = "http://" + localIp() + ":8081/video"
    private fun refresh() {
        val lock = if (!granted) " (grant cam+mic first)" else ""
        camBtn.text = (if (camOn) "Stop Camera" else "Start Camera") + lock
        micBtn.text = (if (micOn) "Stop Mic" else "Start Mic") + lock
        status.text = "cam=${if (camOn) "ON" else "off"} mic=${if (micOn) "ON" else "off"}\n" + base()
    }

    private fun localIp(): String {
        try {
            for (ni in NetworkInterface.getNetworkInterfaces())
                for (a in ni.inetAddresses)
                    if (a is Inet4Address && !a.isLoopbackAddress) return a.hostAddress ?: ""
        } catch (_: Exception) {}
        return "phone-ip"
    }

    override fun onDestroy() {
        mjpeg.stop(); audio.stop(); signal.stop()
        super.onDestroy()
    }
}
