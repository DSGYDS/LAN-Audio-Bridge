package com.lanbridge.app.net

import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull

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
 * - 每次握手/热切独立创建和释放 UdpTransport，不保留状态
 * - 超时 500ms，超时返回 false
 * - 所有异常统一捕获返回 false，不在这个类里做重试（重试由 ReconnectionManager 负责）
 */
object HandshakeManager {

    private const val TAG = "HandshakeManager"
    private const val HANDSHAKE_PORT = 12347
    private const val TIMEOUT_MS = 500L
    private val protocol = PacketHeaderAdapter()

    /**
     * 握手 — 发 HELLO 到电脑，等 HELLO_ACK
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     * @return true 表示握手成功（收到 HELLO_ACK）
     */
    fun handshake(host: String, route: Int): Boolean {
        var transport: UdpTransport? = null
        return try {
            transport = UdpTransport(localPort = 0, remoteHost = host, remotePort = HANDSHAKE_PORT)
            val reply = CompletableDeferred<ByteArray?>()
            transport.onPacketReceived = { data -> reply.complete(data) }
            runBlocking { transport.connect() }

            // 编码并发送 HELLO 包
            val helloPacket = Packet(PacketType.HELLO, 0u, byteArrayOf(route.toByte()))
            transport.sendBlocking(protocol.encode(helloPacket))

            // 等待 HELLO_ACK（500ms 超时）
            val response = runBlocking { withTimeoutOrNull(TIMEOUT_MS) { reply.await() } }
            if (response == null) return false

            // 解析回复
            val decoded = protocol.decode(response)
            decoded != null && decoded.type == PacketType.HELLO_ACK
        } catch (e: Exception) {
            Log.e(TAG, "handshake error: ${e.message}")
            false
        } finally {
            transport?.let { runBlocking { it.disconnect() } }
        }
    }

    /**
     * 推流中切换路线 — 发 ROUTE 通知电脑
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     */
    fun sendRouteUpdate(host: String, route: Int) {
        var transport: UdpTransport? = null
        try {
            transport = UdpTransport(localPort = 0, remoteHost = host, remotePort = HANDSHAKE_PORT)
            runBlocking { transport.connect() }

            // 编码并发送 ROUTE 包
            val routePacket = Packet(PacketType.ROUTE, 0u, byteArrayOf(route.toByte()))
            transport.sendBlocking(protocol.encode(routePacket))
        } catch (e: Exception) {
            Log.e(TAG, "sendRouteUpdate error: ${e.message}")
        } finally {
            transport?.let { runBlocking { it.disconnect() } }
        }
    }
}
