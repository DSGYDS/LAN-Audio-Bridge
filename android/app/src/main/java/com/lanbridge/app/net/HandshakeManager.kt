package com.lanbridge.app.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * HandshakeManager — 握手与热切管理器
 *
 * ## 职责
 * 1. 发 HELLO（二进制 PacketHeader）到电脑 12347 端口，等 HELLO_ACK
 * 2. 推流中发 ROUTE 热切路线
 *
 * ## 路线映射
 * - 0 = 系统音频→扬声器
 * - 1 = 混音→扬声器
 * - 2 = 麦克风→虚拟麦克风
 * - 3 = 系统音频→虚拟麦克风
 *
 * ## 约束
 * - 每次握手/热切独立创建和释放 socket，不保留状态
 * - 超时 500ms，超时返回 false
 * - 所有异常统一捕获返回 false，不在这个类里做重试（重试由 ReconnectionManager 负责）
 */
object HandshakeManager {

    private const val HANDSHAKE_PORT = 12347
    private const val TIMEOUT_MS = 500

    /**
     * 握手 — 发 HELLO 到电脑，等 HELLO_ACK
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     * @return true 表示握手成功（收到 HELLO_ACK）
     */
    fun handshake(host: String, route: Int): Boolean {
        var sock: DatagramSocket? = null
        return try {
            sock = DatagramSocket()
            sock.soTimeout = TIMEOUT_MS
            val addr = InetAddress.getByName(host)

            // 编码 HELLO 包: PacketHeader(HELLO, seq=0, payloadLen=1) + 1B routeMode
            val header = PacketHeader.encodeHeader(PacketType.HELLO.code, seq = 0, payloadLen = 1)
            val payload = byteArrayOf(route.toByte())
            val pktData = header + payload
            sock.send(DatagramPacket(pktData, pktData.size, addr, HANDSHAKE_PORT))

            // 收 HELLO_ACK
            val buf = ByteArray(PacketHeader.HEADER_SIZE + 4) // 多留 4B 余量
            val pkt = DatagramPacket(buf, buf.size)
            sock.receive(pkt)
            val reply = buf.copyOf(pkt.length)
            val info = PacketHeader.tryDecode(reply)
            info != null && info.type == PacketType.HELLO_ACK.code
        } catch (_: Exception) { false } finally { sock?.close() }
    }

    /**
     * 推流中切换路线 — 发 ROUTE 通知电脑
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     */
    fun sendRouteUpdate(host: String, route: Int) {
        var sock: DatagramSocket? = null
        try {
            sock = DatagramSocket()
            val addr = InetAddress.getByName(host)

            // 编码 ROUTE 包: PacketHeader(ROUTE, seq=0, payloadLen=1) + 1B newRouteMode
            val header = PacketHeader.encodeHeader(PacketType.ROUTE.code, seq = 0, payloadLen = 1)
            val payload = byteArrayOf(route.toByte())
            val pktData = header + payload
            sock.send(DatagramPacket(pktData, pktData.size, addr, HANDSHAKE_PORT))
        } catch (_: Exception) {} finally { sock?.close() }
    }
}
