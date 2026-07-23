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
    private const val TIMEOUT_MS = 500L       // LAN 模式超时
    private const val P2P_TIMEOUT_MS = 3000L  // P2P 模式超时（链路延迟较高）
    private val protocol = PlatformFactory.createProtocol()

    /** P2P 模式下 Android 端 P2P 接口本地 IP（LAN 模式为 null） */
    var p2pLocalIp: String? = null

    /** QR 码中的 Windows P2P IP（备用目标，当 goIp 不通时回退） */
    var p2pQrHostIp: String? = null

    /** 最近一次收到 HELLO 的远端 IP（用于确定 Windows P2P 客户端 IP） */
    var lastRemoteIp: String? = null

    /**
     * 等待 Windows 发来的 HELLO（P2P 反转模式：Android 做 GO，Windows 主动握手）
     * 监听 UDP 12347，收到 HELLO 后校验 token 并回复 HELLO_ACK（携带当前 route）
     *
     * @param expectedToken 期望的 token（从 QR 码解析）
     * @param route 当前选择的路线（0-3），通过 HELLO_ACK 告知 Windows
     * @return true 表示握手成功
     */
    fun waitForHello(expectedToken: String?, route: Int = 0): Boolean {
        var transport: UdpTransport? = null
        return try {
            Log.i(TAG, "waitForHello: listening on 0.0.0.0:$HANDSHAKE_PORT, token=${expectedToken?.take(4)}...")
            // 服务端模式：监听 12347
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, port = HANDSHAKE_PORT
            ) as UdpTransport
            val helloReceived = CompletableDeferred<Triple<ByteArray, String, Int>>()  // data + remote IP + remote port

            transport.onPacketReceived = { data ->
                // 从 DatagramPacket 获取远端地址和端口（通过 UdpTransport 的 server 模式）
                val remoteIp = transport.lastRemoteHost ?: "unknown"
                val remotePort = transport.lastRemotePort
                helloReceived.complete(Triple(data, remoteIp, remotePort))
            }
            runBlocking { transport.connect() }

            // 等待 HELLO（最多 60s，等 Windows 连接 P2P 并发起握手）
            val result = runBlocking { withTimeoutOrNull(60_000L) { helloReceived.await() } }
            if (result == null) {
                Log.w(TAG, "waitForHello: TIMEOUT 60s, no HELLO received")
                return false
            }

            val (data, remoteIp, remotePort) = result
            lastRemoteIp = remoteIp
            Log.i(TAG, "waitForHello: received packet from $remoteIp:$remotePort, decoding...")

            val decoded = protocol.decode(data)
            if (decoded == null || decoded.type != PacketType.HELLO) {
                Log.w(TAG, "waitForHello: not a HELLO packet, type=${decoded?.type}")
                return false
            }

            // Token 校验
            val payload = decoded.payload
            if (expectedToken != null) {
                if (payload.size < 9) {
                    Log.w(TAG, "waitForHello: missing token in HELLO")
                    return false
                }
                val tokenStr = String(payload, 1, 8, Charsets.US_ASCII)
                if (tokenStr != expectedToken) {
                    Log.w(TAG, "waitForHello: token mismatch (got=$tokenStr)")
                    return false
                }
            }

            // 回复 HELLO_ACK 到 HELLO 的来源端口（非固定 12347，Windows 从随机端口发来）
            val ackPayload = byteArrayOf(route.toByte())
            val ackPacket = Packet(PacketType.HELLO_ACK, LinkType.WIFI_DIRECT, 0u, ackPayload)
            transport.sendTo(protocol.encode(ackPacket), remoteIp, remotePort)
            Log.i(TAG, "waitForHello: HELLO_ACK sent to $remoteIp:$remotePort (route=$route), handshake OK")
            true
        } catch (e: Exception) {
            Log.e(TAG, "waitForHello error: ${e.message}")
            false
        } finally {
            transport?.let { runBlocking { it.disconnect() } }
        }
    }

    /**
     * 握手 — 发 HELLO 到电脑，等 HELLO_ACK
     *
     * @param host 目标 IP
     * @param route 路线编号 0~3
     * @param token P2P 模式下的认证 token（LAN 模式传 null）
     * @param linkType 链路类型（默认 WiFi LAN）
     * @return true 表示握手成功（收到 HELLO_ACK）
     */
    fun handshake(host: String, route: Int, token: String? = null, linkType: Byte = LinkType.WIFI_LAN): Boolean {
        // P2P 模式：先试主目标，失败后试 QR host（如果不同）
        val result = tryHandshake(host, route, token, linkType)
        if (result) return true

        // 回退：尝试 QR 中的 host IP（可能与 goIp 不同）
        val fallback = p2pQrHostIp
        if (linkType == LinkType.WIFI_DIRECT && fallback != null && fallback != host) {
            Log.i(TAG, "Primary target failed, trying QR host fallback: $fallback")
            return tryHandshake(fallback, route, token, linkType)
        }
        return false
    }

    /**
     * 单次握手尝试 — 发 HELLO 到指定 IP，等 HELLO_ACK
     * P2P 模式下重发 3 次（ARP 解析可能丢弃第一包）
     */
    private fun tryHandshake(host: String, route: Int, token: String?, linkType: Byte): Boolean {
        var transport: UdpTransport? = null
        return try {
            Log.i(TAG, "tryHandshake: host=$host, route=$route, token=${token?.take(4)}..., linkType=$linkType, p2pLocalIp=$p2pLocalIp")
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, host = host, port = HANDSHAKE_PORT,
                localBindAddress = p2pLocalIp
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
            val timeout = if (p2pLocalIp != null) P2P_TIMEOUT_MS else TIMEOUT_MS

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
     */
    fun sendRouteUpdate(host: String, route: Int, linkType: Byte = LinkType.WIFI_LAN) {
        var transport: UdpTransport? = null
        try {
            transport = PlatformFactory.createTransport(
                type = TransportType.Udp, host = host, port = HANDSHAKE_PORT,
                localBindAddress = p2pLocalIp
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
