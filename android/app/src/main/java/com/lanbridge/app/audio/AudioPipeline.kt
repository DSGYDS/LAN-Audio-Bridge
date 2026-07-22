package com.lanbridge.app.audio

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.PacketType
import kotlinx.coroutines.runBlocking

/**
 * 音频管线 — 采集 → Opus 编码 → UDP 发送
 *
 * 三种模式:
 *   MODE_MIC    = 仅麦克风
 *   MODE_SYSTEM = 仅系统音频
 *   MODE_MIX    = 混音（系统 + 麦克风）
 *
 * 调用流程:
 *   startStreaming(mode, ...) → 启动推流
 *   switchMode(newMode, ...)  → 推流中换采集源（热切换）
 *   stopStreaming()           → 停止推流
 */
class AudioPipeline {

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

    // ── 子模块（通过 AudioConfig 初始化） ──
    private val mic: MicrophoneCapturer
    private val sys: SystemAudioCapturer
    private val enc: AudioEncoder
    private var transport: UdpTransport? = null    // ITransport 实现（UDP 发送）
    private val protocol = PlatformFactory.createProtocol()  // 协议编解码

    /** 构造 AudioPipeline，接收 AudioConfig 参数 */
    constructor(config: AudioConfig = AudioConfig.DEFAULT) {
        mic = MicrophoneCapturer(config)
        sys = SystemAudioCapturer(config)
        enc = AudioEncoder(config)
    }

    // ── 生命周期标志 ──
    @Volatile private var streaming = false    // 推流运行中
    @Volatile private var stopping = false     // 请求停止（用于线程退出）
    @Volatile private var mode = MODE_MIC      // 当前采集模式
    private var cb: ((ByteArray, Int) -> Unit)? = null  // 原始 Opus 回调（调试用）
    private var thread: Thread? = null
    private val frameAssembler = PcmFrameAssembler(FRAME_BYTES)  // PCM 拼帧器

    /** 第一帧 Opus 发送成功后的回调（仅触发一次，旁路观测） */
    @Volatile var onFirstFrame: (() -> Unit)? = null

    fun setOnOpusData(cb: (ByteArray, Int) -> Unit) { this.cb = cb }

    // ── 启动推流 ──
    fun startStreaming(m: Int = MODE_MIC, proj: MediaProjection? = null, ctx: Context? = null, host: String? = null, port: Int = 12345): Boolean {
        if (streaming) return true
        if (!enc.prepare()) return false
        if (host != null) {
            try {
                val t = UdpTransport(localPort = 0, remoteHost = host, remotePort = port)
                runBlocking { t.connect() }
                transport = t
            } catch (e: Exception) {
                Log.e(TAG, "Transport connect failed: ${e.message}")
                enc.release()
                return false
            }
        }
        mode = m; stopping = false; streaming = true
        frameAssembler.reset()
        val started = startCapture(m, proj, ctx)
        if (!started) {
            streaming = false
            releaseTransport()
            enc.release()
        }
        return started
    }

    // ── 推流中切换采集模式 ──
    fun switchMode(m: Int, proj: MediaProjection? = null, ctx: Context? = null): Boolean {
        if (!streaming) { mode = m; return true }
        if (m == mode) return true
        if ((m == MODE_SYSTEM || m == MODE_MIX) && proj == null) {
            Log.w(TAG, "switchMode: cannot switch to mode $m without MediaProjection")
            return false
        }
        // 切回纯麦克风时释放系统音频
        val releaseSystemAudio = (mode == MODE_SYSTEM || mode == MODE_MIX) && m == MODE_MIC
        stopCapture(releaseSystemAudio)
        mode = m
        return startCapture(m, proj, ctx)
    }

    // ── 启动采集线程 ──
    // 根据模式创建不同的采集循环：
    //   MODE_MIC/SYSTEM → captureLoop 单源循环
    //   MODE_MIX       → 内联混音循环（两路同时读，混合后编码）
    private fun startCapture(m: Int, proj: MediaProjection? = null, ctx: Context? = null): Boolean {
        stopping = false
        frameAssembler.reset()
        thread = when (m) {
            MODE_MIC -> {
                if (!mic.prepare()) { streaming = false; return false }
                mic.start()
                Thread({ captureLoop { mic.readFrame() } }, "cap-mic")
            }
            MODE_SYSTEM -> {
                if (proj == null || !sys.prepare(proj, ctx)) { streaming = false; return false }
                sys.start()
                Thread({ captureLoop { sys.readFrame() } }, "cap-sys")
            }
            MODE_MIX -> {
                if (proj == null || !sys.prepare(proj, ctx)) { streaming = false; return false }
                if (!mic.prepare()) {
                    sys.release()
                    streaming = false
                    return false
                }
                mic.start()
                sys.start()
                Thread({
                    var seq = 0
                    var failCount = 0
                    var firstFrameNotified = false
                    while (streaming) {
                        if (stopping) { Thread.sleep(1); continue }
                        val a = mic.readFrame()
                        val b = sys.readFrame()
                        val pcmPart = when {
                            a != null && b != null -> AudioMixer.mix(a, b)    // 两路都读到 → 混音
                            a != null -> a                                    // 只读到麦克风
                            b != null -> b                                    // 只读到系统音频
                            else -> {
                                failCount++
                                if (failCount > 10) { Thread.sleep(2); failCount = 0 }
                                continue
                            }
                        }
                        failCount = 0
                        frameAssembler.push(pcmPart) { pcm ->
                            val opus = enc.encodeFrame(pcm)
                            if (opus != null) {
                                sendOpusFrame(opus, seq)
                                if (!firstFrameNotified) {
                                    firstFrameNotified = true
                                    onFirstFrame?.invoke()
                                }
                                cb?.invoke(opus, seq)
                                seq++
                            }
                        }
                    }
                }, "cap-mix")
            }
            else -> null
        }
        val captureThread = thread ?: run { streaming = false; return false }
        captureThread.start()
        return true
    }

    // ── 单源采集循环（MIC / SYSTEM 共用） ──
    // 循环读取 PCM → 拼帧 → Opus 编码 → UDP 发送
    private fun captureLoop(reader: () -> ByteArray?) {
        var seq = 0
        var failCount = 0
        var firstFrameNotified = false
        while (streaming) {
            if (stopping) { Thread.sleep(1); continue }
            val pcm = reader()
            if (pcm != null) {
                failCount = 0
                frameAssembler.push(pcm) { frame ->
                    val opus = enc.encodeFrame(frame)
                    if (opus != null) {
                        sendOpusFrame(opus, seq)
                        if (!firstFrameNotified) {
                            firstFrameNotified = true
                            onFirstFrame?.invoke()
                        }
                        cb?.invoke(opus, seq)
                        seq++
                    }
                }
            } else {
                failCount++
                if (failCount > 10) { Thread.sleep(2); failCount = 0 }
            }
        }
    }

    /** 通过 ITransport 发送一个 Opus 帧（协议编码 + UDP 发送） */
    private fun sendOpusFrame(opus: ByteArray, seq: Int) {
        val t = transport ?: return
        val packet = Packet(PacketType.AUDIO, seq.toUShort(), opus)
        val encoded = protocol.encode(packet)
        t.sendBlocking(encoded)
    }

    /** 停止当前采集线程（供 switchMode 使用，不释放全部资源） */
    private fun stopCapture(releaseSystemAudio: Boolean = false) {
        stopping = true
        streaming = false
        thread?.join(500)
        thread = null
        streaming = true   // 重置以便 switchMode 继续使用
        mic.stop()
        if (releaseSystemAudio) sys.release() else sys.stop()
    }

    // ── 音量控制（暴露到底层采集器） ──
    fun setSysVolume(v: Float) { sys.volume = v.coerceIn(0f, 1f) }
    fun setMicVolume(v: Float) { mic.volume = v.coerceIn(0f, 1f) }

    fun isStreaming() = streaming
    fun getMode() = mode

    /** 完全停止推流，释放所有资源 */
    fun stopStreaming() {
        stopping = true
        streaming = false
        thread?.join(500)
        thread = null
        releaseTransport()
        mic.stop(); mic.release()
        sys.stop(); sys.release()
        enc.release()
        Log.i(TAG, "stopped")
    }

    /** 释放 Transport 资源 */
    private fun releaseTransport() {
        transport?.let { t ->
            runBlocking { t.disconnect() }
        }
        transport = null
    }



    // ── PCM 拼帧器 ──
    // 采集器返回的数据可能是任意长度，拼帧器将其拼成固定 1920 字节帧
    private class PcmFrameAssembler(private val frameBytes: Int) {
        private val pending = ByteArray(frameBytes)
        private var pendingLen = 0

        fun push(data: ByteArray, onFrame: (ByteArray) -> Unit) {
            var offset = 0
            while (offset < data.size) {
                val n = minOf(frameBytes - pendingLen, data.size - offset)
                System.arraycopy(data, offset, pending, pendingLen, n)
                pendingLen += n
                offset += n
                if (pendingLen == frameBytes) {
                    onFrame(pending.copyOf())
                    pendingLen = 0
                }
            }
        }

        fun reset() { pendingLen = 0 }
    }
}
