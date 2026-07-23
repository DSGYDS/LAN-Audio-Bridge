package com.lanbridge.app.audio

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.core.adapters.MicCapturerAdapter
import com.lanbridge.app.core.adapters.SystemAudioCapturerAdapter
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.net.LinkType

/**
 * AudioPipeline — 音频管线编排层
 *
 * 职责：对外 API（startStreaming/switchMode/stopStreaming），
 * 内部协调 CaptureLoop（采集）和 EncodeSender（编码发送）。
 *
 * 三种模式:
 *   MODE_MIC    = 仅麦克风
 *   MODE_SYSTEM = 仅系统音频
 *   MODE_MIX    = 混音（系统 + 麦克风）
 */
class AudioPipeline(config: AudioConfig = AudioConfig.DEFAULT) {

    companion object {
        private const val TAG = "AudioPipeline"
        const val SAMPLE_RATE = 48000
        const val FRAME_MS = 20
        val FRAME_SIZE = SAMPLE_RATE * FRAME_MS / 1000  // 960 采样点
        val FRAME_BYTES = FRAME_SIZE * 2                 // 1920 字节

        const val MODE_MIC = 0
        const val MODE_SYSTEM = 1
        const val MODE_MIX = 2
    }

    // ── 子模块 ──
    private val encodeSender = EncodeSender(config)
    private val captureLoop = CaptureLoop(config) { pcm -> encodeSender.feed(pcm) }

    // ── 状态 ──
    @Volatile private var mode = MODE_MIC
    @Volatile var currentLinkType: Byte = LinkType.WIFI_LAN
        set(value) { field = value; encodeSender.linkType = value }

    /** 第一帧 Opus 发送成功后的回调（仅触发一次） */
    @Volatile var onFirstFrame: (() -> Unit)? = null
        set(value) { field = value; encodeSender.onFirstFrame = value }

    fun setOnOpusData(cb: (ByteArray, Int) -> Unit) { encodeSender.onOpusData = cb }

    // ── 启动推流 ──
    fun startStreaming(m: Int = MODE_MIC, proj: MediaProjection? = null, ctx: Context? = null, host: String? = null, port: Int = 12345, localBindAddress: String? = null): Boolean {
        if (isStreaming()) return true
        if (!encodeSender.prepare(host, port, localBindAddress)) return false
        encodeSender.reset()
        mode = m
        val started = captureLoop.start(m, proj, ctx)
        if (!started) {
            encodeSender.release()
        }
        return started
    }

    // ── 推流中切换采集模式 ──
    fun switchMode(m: Int, proj: MediaProjection? = null, ctx: Context? = null): Boolean {
        if (!isStreaming()) { mode = m; return true }
        if (m == mode) return true
        if ((m == MODE_SYSTEM || m == MODE_MIX) && proj == null) {
            Log.w(TAG, "switchMode: cannot switch to mode $m without MediaProjection")
            return false
        }
        val releaseSystemAudio = (mode == MODE_SYSTEM || mode == MODE_MIX) && m == MODE_MIC
        captureLoop.stop(releaseSystemAudio)
        encodeSender.reset()
        mode = m
        return captureLoop.start(m, proj, ctx)
    }

    // ── 停止推流 ──
    fun stopStreaming() {
        captureLoop.release()
        encodeSender.release()
        Log.i(TAG, "stopped")
    }

    // ── 音量控制 ──
    fun setSysVolume(v: Float) { (captureLoop.sys as? SystemAudioCapturerAdapter)?.volume = v.coerceIn(0f, 1f) }
    fun setMicVolume(v: Float) { (captureLoop.mic as? MicCapturerAdapter)?.volume = v.coerceIn(0f, 1f) }

    // ── 状态查询 ──
    fun isStreaming() = captureLoop.streaming
    fun getMode() = mode
}
