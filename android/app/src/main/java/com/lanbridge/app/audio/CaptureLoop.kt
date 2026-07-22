package com.lanbridge.app.audio

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.core.adapters.MicCapturerAdapter
import com.lanbridge.app.core.adapters.SystemAudioCapturerAdapter
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.IAudioCapturer

/**
 * CaptureLoop — 采集循环 + 看门狗
 *
 * 职责：管理采集线程生命周期，单源/混音采集循环，看门狗重启。
 * 不关心编码和发送，只产出 PCM 帧通过回调交给 EncodeSender。
 */
class CaptureLoop(
    private val config: AudioConfig,
    private val onPcmFrame: (ByteArray) -> Unit
) {
    companion object {
        private const val TAG = "CaptureLoop"
        private const val WATCHDOG_NULL_THRESHOLD = 15   // 连续 15 次 null（≈300ms）触发重启
        private const val WATCHDOG_MAX_RESTARTS = 5      // 最多重启次数
    }

    @Volatile var streaming = false
        private set
    @Volatile var stopping = false

    private var thread: Thread? = null
    private var micCapturer: IAudioCapturer? = null
    private var sysCapturer: IAudioCapturer? = null

    /** 获取当前持有的 capturer 引用（供音量控制） */
    val mic: IAudioCapturer? get() = micCapturer
    val sys: IAudioCapturer? get() = sysCapturer

    /**
     * 启动采集线程
     * @return true 表示成功启动
     */
    fun start(mode: Int, proj: MediaProjection?, ctx: Context?): Boolean {
        stopping = false
        streaming = true
        thread = when (mode) {
            AudioPipeline.MODE_MIC -> {
                val mic = MicCapturerAdapter()
                if (!mic.prepare(config)) { streaming = false; return false }
                mic.start()
                micCapturer = mic
                Thread({ mic.warmup(); singleLoop(mic) }, "cap-mic")
            }
            AudioPipeline.MODE_SYSTEM -> {
                if (proj == null) { streaming = false; return false }
                val sys = SystemAudioCapturerAdapter(proj, ctx)
                if (!sys.prepare(config)) { streaming = false; return false }
                sys.start()
                sysCapturer = sys
                Thread({ sys.warmup(); singleLoop(sys) }, "cap-sys")
            }
            AudioPipeline.MODE_MIX -> {
                if (proj == null) { streaming = false; return false }
                val sys = SystemAudioCapturerAdapter(proj, ctx)
                val mic = MicCapturerAdapter()
                if (!sys.prepare(config)) { streaming = false; return false }
                if (!mic.prepare(config)) { sys.release(); streaming = false; return false }
                mic.start()
                sys.start()
                micCapturer = mic
                sysCapturer = sys
                Thread({ mic.warmup(); sys.warmup(); mixLoop(mic, sys) }, "cap-mix")
            }
            else -> null
        }
        val t = thread ?: run { streaming = false; return false }
        t.start()
        return true
    }

    /** 停止采集线程（供 switchMode 用，不释放全部资源） */
    fun stop(releaseSystemAudio: Boolean = false) {
        stopping = true
        streaming = false
        thread?.join(500)
        thread = null
        streaming = true  // 重置以便 switchMode 继续使用
        micCapturer?.stop()
        if (releaseSystemAudio) { sysCapturer?.release(); sysCapturer = null }
        else sysCapturer?.stop()
    }

    /** 完全释放（stopStreaming 时） */
    fun release() {
        stopping = true
        streaming = false
        thread?.join(500)
        thread = null
        micCapturer?.stop(); micCapturer?.release(); micCapturer = null
        sysCapturer?.stop(); sysCapturer?.release(); sysCapturer = null
    }

    // ── 单源采集循环（MIC / SYSTEM 共用） ──

    private fun singleLoop(capturer: IAudioCapturer) {
        var failCount = 0
        var watchdogCount = 0
        var restartAttempts = 0
        val buf = ByteArray(AudioPipeline.FRAME_BYTES)

        while (streaming) {
            if (stopping) { Thread.sleep(1); continue }
            val n = capturer.readFrame(buf, 0, AudioPipeline.FRAME_BYTES)
            if (n > 0) {
                failCount = 0
                watchdogCount = 0
                restartAttempts = 0
                val pcm = if (n == AudioPipeline.FRAME_BYTES) buf else buf.copyOf(n)
                onPcmFrame(pcm)
            } else {
                failCount++
                watchdogCount++
                if (failCount > 10) { Thread.sleep(2); failCount = 0 }
                if (watchdogCount >= WATCHDOG_NULL_THRESHOLD && restartAttempts < WATCHDOG_MAX_RESTARTS) {
                    restartAttempts++
                    Log.w(TAG, "Watchdog: restarting capturer (attempt $restartAttempts)")
                    if (capturer.restart()) watchdogCount = 0
                    else Thread.sleep(50)
                }
            }
        }
        Log.i(TAG, "singleLoop exited")
    }

    // ── 混音采集循环（MODE_MIX） ──

    private fun mixLoop(mic: IAudioCapturer, sys: IAudioCapturer) {
        var failCount = 0
        var watchdogCount = 0
        var restartAttempts = 0
        val bufA = ByteArray(AudioPipeline.FRAME_BYTES)
        val bufB = ByteArray(AudioPipeline.FRAME_BYTES)

        while (streaming) {
            if (stopping) { Thread.sleep(1); continue }
            val nA = mic.readFrame(bufA, 0, AudioPipeline.FRAME_BYTES)
            val nB = sys.readFrame(bufB, 0, AudioPipeline.FRAME_BYTES)
            val pcmPart = when {
                nA > 0 && nB > 0 -> AudioMixer.mix(
                    if (nA == AudioPipeline.FRAME_BYTES) bufA else bufA.copyOf(nA),
                    if (nB == AudioPipeline.FRAME_BYTES) bufB else bufB.copyOf(nB)
                )
                nA > 0 -> if (nA == AudioPipeline.FRAME_BYTES) bufA else bufA.copyOf(nA)
                nB > 0 -> if (nB == AudioPipeline.FRAME_BYTES) bufB else bufB.copyOf(nB)
                else -> {
                    failCount++
                    watchdogCount++
                    if (failCount > 10) { Thread.sleep(2); failCount = 0 }
                    if (watchdogCount >= WATCHDOG_NULL_THRESHOLD && restartAttempts < WATCHDOG_MAX_RESTARTS) {
                        restartAttempts++
                        Log.w(TAG, "Mix watchdog: restarting capturers (attempt $restartAttempts)")
                        mic.restart()
                        sys.restart()
                        watchdogCount = 0
                    }
                    continue
                }
            }
            failCount = 0
            watchdogCount = 0
            restartAttempts = 0
            onPcmFrame(pcmPart)
        }
        Log.i(TAG, "mixLoop exited")
    }
}
