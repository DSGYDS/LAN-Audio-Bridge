package com.lanbridge.app.audio

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.core.adapters.MicCapturerAdapter
import com.lanbridge.app.core.adapters.SystemAudioCapturerAdapter
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.IAudioCapturer
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.LinkType
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

        // 看门狗参数
        private const val WATCHDOG_NULL_THRESHOLD = 15   // 连续 15 次 null（≈300ms）触发重启
        private const val WATCHDOG_MAX_RESTARTS = 5      // 最多重启次数
    }

    // ── 子模块（通过 IAudioCapturer 接口） ──
    private val config: AudioConfig
    private var micCapturer: IAudioCapturer? = null   // 麦克风采集（IAudioCapturer）
    private var sysCapturer: IAudioCapturer? = null   // 系统音频采集（IAudioCapturer）
    private val enc: AudioEncoder
    private var transport: UdpTransport? = null    // ITransport 实现（UDP 发送）
    private val protocol = PlatformFactory.createProtocol()  // 协议编解码

    /** 构造 AudioPipeline，接收 AudioConfig 参数 */
    constructor(config: AudioConfig = AudioConfig.DEFAULT) {
        this.config = config
        enc = AudioEncoder(config)
    }

    // ── 生命周期标志 ──
    @Volatile private var streaming = false    // 推流运行中
    @Volatile private var stopping = false     // 请求停止（用于线程退出）
    @Volatile private var mode = MODE_MIC      // 当前采集模式
    @Volatile var currentLinkType: Byte = LinkType.WIFI_LAN  // 当前链路类型
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
                val t = PlatformFactory.createTransport(
                    type = TransportType.Udp,
                    host = host,
                    port = port,
                    localBindAddress = com.lanbridge.app.net.HandshakeManager.p2pLocalIp
                ) as UdpTransport
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
                val mic = MicCapturerAdapter()
                if (!mic.prepare(config)) { streaming = false; return false }
                mic.start()
                micCapturer = mic
                Thread({ mic.warmup(); captureLoop(mic) }, "cap-mic")
            }
            MODE_SYSTEM -> {
                if (proj == null) { streaming = false; return false }
                val sys = SystemAudioCapturerAdapter(proj, ctx)
                if (!sys.prepare(config)) { streaming = false; return false }
                sys.start()
                sysCapturer = sys
                Thread({ sys.warmup(); captureLoop(sys) }, "cap-sys")
            }
            MODE_MIX -> {
                if (proj == null) { streaming = false; return false }
                val sys = SystemAudioCapturerAdapter(proj, ctx)
                val mic = MicCapturerAdapter()
                if (!sys.prepare(config)) { streaming = false; return false }
                if (!mic.prepare(config)) {
                    sys.release()
                    streaming = false
                    return false
                }
                mic.start()
                sys.start()
                micCapturer = mic
                sysCapturer = sys
                Thread({
                    mic.warmup(); sys.warmup()  // HAL 预热
                    var seq = 0
                    var failCount = 0
                    var firstFrameNotified = false
                    var watchdogCount = 0
                    var restartAttempts = 0
                    val bufA = ByteArray(FRAME_BYTES)
                    val bufB = ByteArray(FRAME_BYTES)
                    while (streaming) {
                        if (stopping) { Thread.sleep(1); continue }
                        val nA = mic.readFrame(bufA, 0, FRAME_BYTES)
                        val nB = sys.readFrame(bufB, 0, FRAME_BYTES)
                        val pcmPart = when {
                            nA > 0 && nB > 0 -> AudioMixer.mix(
                                if (nA == FRAME_BYTES) bufA else bufA.copyOf(nA),
                                if (nB == FRAME_BYTES) bufB else bufB.copyOf(nB)
                            )
                            nA > 0 -> if (nA == FRAME_BYTES) bufA else bufA.copyOf(nA)
                            nB > 0 -> if (nB == FRAME_BYTES) bufB else bufB.copyOf(nB)
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
    // 通过 IAudioCapturer 接口读取 PCM → 拼帧 → Opus 编码 → UDP 发送
    // 看门狗：连续无帧达阈值时自动重启采集器
    private fun captureLoop(capturer: IAudioCapturer) {
        var seq = 0
        var failCount = 0
        var firstFrameNotified = false
        var watchdogCount = 0
        var restartAttempts = 0
        var encodeNullCount = 0
        val loopStart = System.currentTimeMillis()
        val buf = ByteArray(FRAME_BYTES)
        while (streaming) {
            if (stopping) { Thread.sleep(1); continue }
            val n = capturer.readFrame(buf, 0, FRAME_BYTES)
            if (n > 0) {
                failCount = 0
                watchdogCount = 0
                restartAttempts = 0
                val pcm = if (n == FRAME_BYTES) buf else buf.copyOf(n)
                frameAssembler.push(pcm) { frame ->
                    val opus = enc.encodeFrame(frame)
                    if (opus != null) {
                        sendOpusFrame(opus, seq)
                        if (!firstFrameNotified) {
                            firstFrameNotified = true
                            Log.i(TAG, "First opus frame sent: seq=0, size=${opus.size}, elapsed=${System.currentTimeMillis() - loopStart}ms")
                            onFirstFrame?.invoke()
                        }
                        cb?.invoke(opus, seq)
                        seq++
                        if (seq % 250 == 0) {
                            Log.i(TAG, "captureLoop stats: sent=$seq, encodeNull=$encodeNullCount, elapsed=${System.currentTimeMillis() - loopStart}ms")
                        }
                    } else {
                        encodeNullCount++
                        if (encodeNullCount <= 3 || encodeNullCount % 100 == 0) {
                            Log.w(TAG, "encodeFrame returned null (count=$encodeNullCount)")
                        }
                    }
                }
            } else {
                failCount++
                watchdogCount++
                if (failCount > 10) { Thread.sleep(2); failCount = 0 }
                if (watchdogCount >= WATCHDOG_NULL_THRESHOLD && restartAttempts < WATCHDOG_MAX_RESTARTS) {
                    restartAttempts++
                    Log.w(TAG, "Watchdog: restarting capturer (attempt $restartAttempts)")
                    if (capturer.restart()) {
                        watchdogCount = 0
                    } else {
                        Thread.sleep(50)
                    }
                }
            }
        }
        Log.i(TAG, "captureLoop exited: totalSent=$seq, encodeNull=$encodeNullCount")
    }

    /** 通过 ITransport 发送一个 Opus 帧（协议编码 + UDP 发送） */
    private fun sendOpusFrame(opus: ByteArray, seq: Int) {
        val t = transport ?: return
        val packet = Packet(PacketType.AUDIO, currentLinkType, seq.toUShort(), opus)
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
        micCapturer?.stop()
        if (releaseSystemAudio) { sysCapturer?.release(); sysCapturer = null }
        else sysCapturer?.stop()
    }

    // ── 音量控制（通过适配器透传） ──
    fun setSysVolume(v: Float) { (sysCapturer as? SystemAudioCapturerAdapter)?.volume = v.coerceIn(0f, 1f) }
    fun setMicVolume(v: Float) { (micCapturer as? MicCapturerAdapter)?.volume = v.coerceIn(0f, 1f) }

    fun isStreaming() = streaming
    fun getMode() = mode

    /** 完全停止推流，释放所有资源 */
    fun stopStreaming() {
        stopping = true
        streaming = false
        thread?.join(500)
        thread = null
        releaseTransport()
        micCapturer?.stop(); micCapturer?.release(); micCapturer = null
        sysCapturer?.stop(); sysCapturer?.release(); sysCapturer = null
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
