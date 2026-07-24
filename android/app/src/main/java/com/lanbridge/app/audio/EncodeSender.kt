package com.lanbridge.app.audio

import com.lanbridge.app.core.adapters.BluetoothTransport
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.ITransport
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType
import kotlinx.coroutines.runBlocking

/**
 * EncodeSender — 编码 + 发送模块
 *
 * 职责：接收 PCM 帧 → 拼帧 → Opus 编码 → 协议封装 → UDP 发送。
 * 不关心采集，只消费 PCM。
 */
class EncodeSender(private val config: AudioConfig) {

    companion object {
        private const val TAG = "EncodeSender"
    }

    private val enc = AudioEncoder(config)
    private val protocol = PlatformFactory.createProtocol()
    private val assembler = PcmFrameAssembler(AudioPipeline.FRAME_BYTES)
    private var transport: ITransport? = null
    private var seq = 0
    private var firstFrameNotified = false

    /** 当前链路类型（由 AudioPipeline 设置） */
    @Volatile var linkType: Byte = LinkType.WIFI_LAN

    /** 首帧回调（仅触发一次） */
    var onFirstFrame: (() -> Unit)? = null

    /** 原始 Opus 回调（调试用） */
    var onOpusData: ((ByteArray, Int) -> Unit)? = null

    /** 准备编码器 + 创建 Transport */
    fun prepare(host: String?, port: Int, localBindAddress: String? = null): Boolean {
        if (!enc.prepare()) return false
        if (host != null) {
            try {
                val t = PlatformFactory.createTransport(
                    type = TransportType.Udp,
                    host = host,
                    port = port,
                    localBindAddress = localBindAddress
                ) as UdpTransport
                runBlocking { t.connect() }
                transport = t
            } catch (e: Exception) {
                Log.e(TAG, "Transport connect failed: ${e.message}")
                enc.release()
                return false
            }
        }
        return true
    }

    /**
     * 使用外部 Transport 准备（蓝牙链路用）。
     * 不创建 UdpTransport，直接使用传入的已连接 ITransport。
     */
    fun prepareWithTransport(externalTransport: ITransport): Boolean {
        if (!enc.prepare()) return false
        transport = externalTransport
        return true
    }

    /** 喂入一帧 PCM（可能不是 1920 字节，内部拼帧） */
    fun feed(pcm: ByteArray) {
        assembler.push(pcm) { frame ->
            val opus = enc.encodeFrame(frame)
            if (opus != null) {
                sendOpusFrame(opus, seq)
                if (!firstFrameNotified) {
                    firstFrameNotified = true
                    onFirstFrame?.invoke()
                }
                onOpusData?.invoke(opus, seq)
                seq++
            }
        }
    }

    /** 重置拼帧器 + 序号（新会话/切模式时） */
    fun reset() {
        assembler.reset()
        seq = 0
        firstFrameNotified = false
    }

    /** 释放编码器 + Transport */
    fun release() {
        transport?.let { t ->
            // 蓝牙 Transport 由 BluetoothLink 管理生命周期，此处不主动断开
            if (t is UdpTransport) runBlocking { t.disconnect() }
        }
        transport = null
        enc.release()
    }

    private fun sendOpusFrame(opus: ByteArray, seq: Int) {
        val t = transport ?: return
        val packet = Packet(PacketType.AUDIO, linkType, seq.toUShort(), opus)
        val encoded = protocol.encode(packet)
        when (t) {
            is UdpTransport -> t.sendBlocking(encoded)
            is BluetoothTransport -> t.sendBlocking(encoded)
            else -> runBlocking { t.send(encoded) }
        }
    }
}
