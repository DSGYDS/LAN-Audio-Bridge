package com.lanbridge.app.links.bluetooth

import com.lanbridge.app.core.adapters.BluetoothTransport
import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * BtHandshakeClient — 蓝牙链路主动握手（Android 端）
 *
 * 职责：构造 HELLO(route+token) → 发送 → 等待 HELLO_ACK。
 * 与 WifiDirect 的 HandshakeManager.handshake() 对称。
 */
object BtHandshakeClient {

    private const val TAG = "BtHandshake"
    private const val BT_TOKEN = "LABRIDGE"  // 必须 ≤ 8 字符（payload 限制）
    private const val ACK_TIMEOUT_S = 10L
    private const val ACK_MAX_ATTEMPTS = 3

    /**
     * 发送 HELLO 并等待 ACK。
     * 流程：构造 payload[0]=route,[1-8]=token → 发送 → 注册回调等 ACK → 超时重试。
     * 阻塞调用，必须在 IO 线程执行。
     *
     * @return ACK 中的 route（0-3），失败返回 -1
     */
    fun sendHelloAndWaitForAck(transport: BluetoothTransport, route: Int): Int {
        val protocol = PacketHeaderAdapter()

        // 构造 HELLO payload: [0]=route, [1-8]=token ASCII
        val tokenBytes = BT_TOKEN.toByteArray(Charsets.US_ASCII)
        val payload = ByteArray(9)
        payload[0] = route.toByte()
        System.arraycopy(tokenBytes, 0, payload, 1, minOf(8, tokenBytes.size))

        val packet = Packet(PacketType.HELLO, LinkType.BLUETOOTH, 0.toUShort(), payload)
        val encoded = protocol.encode(packet)

        for (attempt in 1..ACK_MAX_ATTEMPTS) {
            val latch = CountDownLatch(1)
            var ackRoute = -1

            transport.onPacketReceived = { data ->
                val decoded = protocol.decode(data)
                if (decoded != null && decoded.type == PacketType.HELLO_ACK) {
                    ackRoute = if (decoded.payload.isNotEmpty())
                        decoded.payload[0].toInt().coerceIn(0, 3) else 0
                    latch.countDown()
                } else if (decoded != null && decoded.type == PacketType.HELLO_NACK) {
                    ackRoute = -1
                    latch.countDown()
                }
            }

            transport.sendBlocking(encoded)
            Log.i(TAG, "HELLO sent (attempt $attempt/$ACK_MAX_ATTEMPTS)")

            val completed = latch.await(ACK_TIMEOUT_S, TimeUnit.SECONDS)
            transport.onPacketReceived = null

            if (completed && ackRoute >= 0) {
                Log.i(TAG, "HELLO_ACK received, route=$ackRoute")
                return ackRoute
            }
            Log.w(TAG, "ACK timeout (attempt $attempt)")
        }

        return -1
    }
}
