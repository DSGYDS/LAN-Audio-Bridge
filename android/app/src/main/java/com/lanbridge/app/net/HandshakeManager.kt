package com.lanbridge.app.net

import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull

/**
 * HandshakeManager — 共享主动握手工具（无状态）
 *
 * ## 职责
 * 1. 发 HELLO 到电脑 12347 端口，等 HELLO_ACK（LAN / P2P 共用）
 * 2. 推流中发 ROUTE 热切路线
 *
 * ## 约束
 * - 无状态：不持有任何链路状态，localBindAddress 由调用方传入
 * - 每次握手/热切独立创建和释放 UdpTransport
 * - 所有异常统一捕获返回 false
 */
object HandshakeManager {

    private const val TAG = "HandshakeManager"
    private const val HANDSHAKE_PORT = 12347
    private const val TIMEOUT_MS = 500L       // LAN 模式超时
    private const val P2P_TIMEOUT_MS = 3000L  // P2P 模式超时（链路延迟较高）
    private val protocol = PlatformFactory.createProtocol()

    /**
     * 握手 — 发 HELLO 到电脑，等 HELLO_ACK
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     * @param token 认证 token（LAN 模式传 null）
     * @param linkType 链路类型
     * @param localBindAddress 本地绑定地址（P2P 模式传 P2P 接口 IP，LAN 传 null）
     * @return true 表示握手成功（收到 HELLO_ACK）
     */
    fun handshake(host: String, route: Int, token: String? = null, linkType: Byte = LinkType.WIFI_LAN, localBindAddress: String? = null): Boolean {
        return tryHandshake(host, route, token, linkType, localBindAddress)
    }

    /**
     * 单次握手尝试 — 发 HELLO 到指定 IP，等 HELLO_ACK
     * P2P 模式下重发 3 次（ARP 解析可能丢弃第一包）
     */
    private fun tryHandshake(host: String, route: Int, token: String?, linkType: Byte, localBindAddress: String?): Boolean {
        var transport: UdpTransport? = null
        return try {
            Log.i(TAG, "tryHandshake: host=$host, route=$route, token=${token?.take(4)}..., linkType=$linkType, bind=$localBindAddress")
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, host = host, port = HANDSHAKE_PORT,
                localBindAddress = localBindAddress
            ) as UdpTransport
            val reply = CompletableDeferred<ByteArray?>()
            transport.onPacketReceived = { data -> reply.complete(data) }
            runBlocking { transport.connect() }
            Log.i(TAG, "tryHandshake: socket connected to $host:$HANDSHAKE_PORT")

            // 编码 HELLO 包（payload: routeMode [+ token]）
            val payload = if (token != null) {
                val tokenBytes = token.toByteArray(Charsets.US_ASCII)
                byteArrayOf(route.toByte()) + tokenBytes.copyOf(8) // 固定 8 字节
            } else {
                byteArrayOf(route.toByte())
            }
            val helloPacket = Packet(PacketType.HELLO, linkType, 0u, payload)
            val encoded = protocol.encode(helloPacket)

            // P2P 模式：重发 3 次（间隔 800ms），ARP 解析可能丢弃前几包
            val attempts = if (linkType == LinkType.WIFI_DIRECT) 3 else 1
            val timeout = if (localBindAddress != null) P2P_TIMEOUT_MS else TIMEOUT_MS

            for (i in 1..attempts) {
                transport.sendBlocking(encoded)
                Log.i(TAG, "tryHandshake: HELLO sent attempt $i/$attempts (${payload.size}B)")

                val waitMs = if (i < attempts) 800L else timeout
                val response = runBlocking { withTimeoutOrNull(waitMs) { reply.await() } }
                if (response != null) {
                    val decoded = protocol.decode(response)
                    Log.i(TAG, "tryHandshake: got reply, type=${decoded?.type}")
                    return decoded != null && decoded.type == PacketType.HELLO_ACK
                }
                if (i < attempts) Log.i(TAG, "tryHandshake: no reply, retrying...")
            }

            Log.w(TAG, "tryHandshake: FAILED after $attempts attempts, no HELLO_ACK from $host:$HANDSHAKE_PORT")
            false
        } catch (e: Exception) {
            Log.e(TAG, "tryHandshake error: ${e.message}")
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
     * @param linkType 链路类型
     * @param localBindAddress 本地绑定地址（P2P 模式传 P2P 接口 IP，LAN 传 null）
     */
    fun sendRouteUpdate(host: String, route: Int, linkType: Byte = LinkType.WIFI_LAN, localBindAddress: String? = null) {
        var transport: UdpTransport? = null
        try {
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, host = host, port = HANDSHAKE_PORT,
                localBindAddress = localBindAddress
            ) as UdpTransport
            runBlocking { transport.connect() }

            // 编码并发送 ROUTE 包
            val routePacket = Packet(PacketType.ROUTE, linkType, 0u, byteArrayOf(route.toByte()))
            transport.sendBlocking(protocol.encode(routePacket))
        } catch (e: Exception) {
            Log.e(TAG, "sendRouteUpdate error: ${e.message}")
        } finally {
            transport?.let { runBlocking { it.disconnect() } }
        }
    }
}
