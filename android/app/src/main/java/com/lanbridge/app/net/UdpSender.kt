package com.lanbridge.app.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * UDP 发送器
 *
 * 包格式: [PacketHeader 14B][Opus 载荷]
 * - Header 包含 Type=AUDIO、Sequence、PayloadLen
 * - Payload 为纯 Opus 编码数据（无 pcmSize 等冗余元数据）
 *
 * 线程安全约定：上层 [AudioPipeline] 保证单线程串行调用
 */
class UdpSender {

    companion object {
        const val DEFAULT_PORT = 12345
    }

    private var sock: DatagramSocket? = null
    private var addr: InetAddress? = null
    private var port = DEFAULT_PORT

    /** 初始化 UDP socket 并绑定目标地址 */
    fun prepare(host: String, p: Int = DEFAULT_PORT): Boolean = try {
        addr = InetAddress.getByName(host)
        port = p
        sock = DatagramSocket()
        true
    } catch (_: Exception) { false }

    /**
     * 发送一个 Opus 音频帧
     * @param opus 编码后的 Opus 包
     * @param seq 帧序号，从 0 递增
     */
    fun sendOpusFrame(opus: ByteArray, seq: Int): Boolean {
        val s = sock ?: return false
        val a = addr ?: return false
        return try {
            // 编码 14B PacketHeader (type=AUDIO)
            val header = PacketHeader.encodeHeader(PacketType.AUDIO.code, seq, opus.size)
            // 拼包: Header + 纯 Opus
            val buf = ByteArray(PacketHeader.HEADER_SIZE + opus.size)
            System.arraycopy(header, 0, buf, 0, PacketHeader.HEADER_SIZE)
            System.arraycopy(opus, 0, buf, PacketHeader.HEADER_SIZE, opus.size)
            s.send(DatagramPacket(buf, buf.size, a, port))
            true
        } catch (_: Exception) { false }
    }

    /** 关闭 UDP socket。释放后可重新 [prepare]。 */
    fun release() {
        try { sock?.close() } catch (_: Exception) {}
        sock = null
    }
}
