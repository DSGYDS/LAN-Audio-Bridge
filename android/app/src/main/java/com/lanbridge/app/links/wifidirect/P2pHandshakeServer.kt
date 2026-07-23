package com.lanbridge.app.links.wifidirect

import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull

/**
 * P2pHandshakeServer — P2P 专属被动握手（Android 做 GO，等 Windows 发 HELLO）
 *
 * 职责单一：监听 UDP 12347，收到 HELLO 后校验 token，回复 HELLO_ACK。
 * 无状态：不持有任何链路状态，结果通过返回值传递。
 */
object P2pHandshakeServer {

    private const val TAG = "P2pHandshakeServer"
    private const val HANDSHAKE_PORT = 12347
    private val protocol = PlatformFactory.createProtocol()

    /**
     * 等待 Windows 发来的 HELLO
     *
     * @param expectedToken 期望的 token（从 QR 码 / 配对存储）
     * @param route 当前路线（0-3），通过 HELLO_ACK 告知 Windows
     * @return 成功时返回 Windows 远端 IP，失败返回 null
     */
    fun waitForHello(expectedToken: String?, route: Int = 0): String? {
        var transport: UdpTransport? = null
        return try {
            Log.i(TAG, "waitForHello: listening on 0.0.0.0:$HANDSHAKE_PORT, token=${expectedToken?.take(4)}...")
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, port = HANDSHAKE_PORT
            ) as UdpTransport
            val helloReceived = CompletableDeferred<Triple<ByteArray, String, Int>>()

            transport.onPacketReceived = { data ->
                val remoteIp = transport.lastRemoteHost ?: "unknown"
                val remotePort = transport.lastRemotePort
                helloReceived.complete(Triple(data, remoteIp, remotePort))
            }
            runBlocking { transport.connect() }

            // 等待 HELLO（最多 60s）
            val result = runBlocking { withTimeoutOrNull(60_000L) { helloReceived.await() } }
            if (result == null) {
                Log.w(TAG, "waitForHello: TIMEOUT 60s, no HELLO received")
                return null
            }

            val (data, remoteIp, remotePort) = result
            Log.i(TAG, "waitForHello: received packet from $remoteIp:$remotePort, decoding...")

            val decoded = protocol.decode(data)
            if (decoded == null || decoded.type != PacketType.HELLO) {
                Log.w(TAG, "waitForHello: not a HELLO packet, type=${decoded?.type}")
                return null
            }

            // Token 校验
            val payload = decoded.payload
            if (expectedToken != null) {
                if (payload.size < 9) {
                    Log.w(TAG, "waitForHello: missing token in HELLO")
                    return null
                }
                val tokenStr = String(payload, 1, 8, Charsets.US_ASCII)
                if (tokenStr != expectedToken) {
                    Log.w(TAG, "waitForHello: token mismatch (got=$tokenStr)")
                    return null
                }
            }

            // 回复 HELLO_ACK 到来源端口
            val ackPayload = byteArrayOf(route.toByte())
            val ackPacket = Packet(PacketType.HELLO_ACK, LinkType.WIFI_DIRECT, 0u, ackPayload)
            transport.sendTo(protocol.encode(ackPacket), remoteIp, remotePort)
            Log.i(TAG, "waitForHello: HELLO_ACK sent to $remoteIp:$remotePort (route=$route), handshake OK")
            remoteIp
        } catch (e: Exception) {
            Log.e(TAG, "waitForHello error: ${e.message}")
            null
        } finally {
            transport?.let { runBlocking { it.disconnect() } }
        }
    }
}
