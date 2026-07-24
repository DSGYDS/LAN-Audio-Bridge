package com.lanbridge.app.links.usb

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionState
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.StreamingService
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.adapters.UsbTransport
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.links.ILink
import com.lanbridge.app.links.LinkManager
import com.lanbridge.app.links.LinkParams
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * UsbLink — USB 链路（Android 端，主动发起方）
 *
 * 职责：用户点击"USB 直连" → TCP 连接 localhost:12348 → 发 HELLO → 推流。
 * 与蓝牙链路对称：手机主动发起连接和握手，Windows 常驻等待。
 *
 * 握手方向：Android 发 HELLO(token+route) → Windows 校验 → 回 HELLO_ACK(route)（与蓝牙一致）
 * 数据通路：AudioPipeline → EncodeSender → UsbTransport.sendBlocking()
 *
 * 依赖：USB 链路专属，与 LAN/P2P/蓝牙完全解耦。
 * 前置条件：USB 线已连接 + USB 调试已开启 + Windows 端已自动建立 adb forward。
 */
class UsbLink(
    private val context: Context,
    private val pipe: AudioPipeline,
    private val stateManager: ConnectionStateManager
) : ILink {

    companion object {
        private const val TAG = "UsbLink"
        const val LINK_TYPE_ID: Byte = LinkType.USB
        private const val USB_TOKEN = "LABRIDGE"  // 必须 ≤ 8 字符（payload 限制）
        private const val ACK_TIMEOUT_S = 10L
        private const val ACK_MAX_ATTEMPTS = 3
    }

    // ── 子模块 ──
    private val connectMutex = Mutex()
    private var usbTransport: UsbTransport? = null

    // ── ILink 状态 ──
    @Volatile override var isStreaming = false
        private set
    override var onStatusChanged: ((String) -> Unit)? = null
    override var onStreamingChanged: ((Boolean) -> Unit)? = null

    @Volatile var currentRoute: Int = 0
        private set

    // ── ILink 实现 ──

    /**
     * 连接 USB 链路（用户点击“USB 直连”触发）
     * 流程：启动 TCP Server → 等待 Windows 连接 → 发 HELLO → 等 ACK → 推流
     */
    override suspend fun connect(params: LinkParams): Boolean = connectMutex.withLock {
        currentRoute = params.route
        onStatusChanged?.invoke("USB：启动监听，请确认 USB 已连接且电脑端已就绪...")
        stateManager.update(ConnectionState.CONNECTING)
    
        // 1. 启动 TCP Server（等待 Windows 通过 adb forward 连入）
        val transport = UsbTransport()
        transport.startListening()
        usbTransport = transport
    
        // 等待 ServerSocket 绑定完成
        withContext(Dispatchers.IO) { Thread.sleep(200) }
    
        // 2. 等待 Windows 连接
        onStatusChanged?.invoke("USB：等待电脑连接...")
        val connected = withContext(Dispatchers.IO) { transport.waitForConnection(timeoutMs = 0) }
        if (!connected) {
            onStatusChanged?.invoke("USB：等待连接超时")
            stateManager.update(ConnectionState.ERROR)
            transport.stopListening()
            usbTransport = null
            return@withLock false
        }
        onStatusChanged?.invoke("USB：电脑已连接，握手中...")

        // 2. 主动发 HELLO → 等待 ACK（与蓝牙 BtHandshakeClient 对称）
        val ackRoute = withContext(Dispatchers.IO) {
            sendHelloAndWaitForAck(transport, params.route)
        }
        if (ackRoute < 0) {
            onStatusChanged?.invoke("USB：握手失败（电脑未响应）")
            stateManager.update(ConnectionState.ERROR)
            disconnect()
            return@withLock false
        }

        stateManager.update(ConnectionState.CONNECTED)
        onStatusChanged?.invoke("USB：握手成功 ✓ 准备推流")

        // 3. 启动推流（注入 UsbTransport）
        val capMode = LinkManager.routeToCapture(params.route)
        val ok = withContext(Dispatchers.IO) {
            pipe.currentLinkType = LINK_TYPE_ID
            pipe.startStreamingWithTransport(transport, capMode, params.proj, context)
        }

        if (ok) {
            isStreaming = true
            pipe.onFirstFrame = { stateManager.update(ConnectionState.STREAMING) }
            onStatusChanged?.invoke("USB 推流中：路线${params.route + 1}")
            onStreamingChanged?.invoke(true)
            context.startForegroundService(Intent(context, StreamingService::class.java))
        } else {
            onStatusChanged?.invoke("USB：启动推流失败")
            stateManager.update(ConnectionState.ERROR)
            disconnect()
        }
        ok
    }

    /** 推流中热切路线：切换采集模式 + 发送 ROUTE 包通知 Windows 切换 AudioRouter */
    override suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        if (!isStreaming) { currentRoute = route; return true }
        currentRoute = route

        val capMode = LinkManager.routeToCapture(route)
        val ok = withContext(Dispatchers.IO) { pipe.switchMode(capMode, proj, context) }
        if (!ok) { onStatusChanged?.invoke("需先授权系统音频"); return false }

        // 发送 ROUTE 包到 Windows
        val transport = usbTransport ?: return false
        withContext(Dispatchers.IO) {
            val protocol = PacketHeaderAdapter()
            val payload = byteArrayOf(route.toByte())
            val packet = Packet(PacketType.ROUTE, LINK_TYPE_ID, 0.toUShort(), payload)
            transport.sendBlocking(protocol.encode(packet))
        }
        return true
    }

    /** 断开 USB 链路：停止推流 + 关闭 TCP Server + 状态回退 */
    override fun disconnect() {
        context.stopService(Intent(context, StreamingService::class.java))
        pipe.stopStreaming()

        usbTransport?.stopListening()
        usbTransport = null

        isStreaming = false
        stateManager.update(ConnectionState.DISCONNECTED)
        onStatusChanged?.invoke("USB：已停止")
        onStreamingChanged?.invoke(false)
    }

    // ── 主动握手：发 HELLO → 等 ACK（与蓝牙 BtHandshakeClient 逻辑对称） ──

    /**
     * 发送 HELLO 并等待 ACK。
     * 流程：构造 payload[0]=route,[1-8]=token → 发送 → 注册回调等 ACK → 超时重试。
     * 阻塞调用，必须在 IO 线程执行。
     * @return ACK 中的 route（0-3），失败返回 -1
     */
    private fun sendHelloAndWaitForAck(transport: UsbTransport, route: Int): Int {
        val protocol = PacketHeaderAdapter()

        // 构造 HELLO payload: [0]=route, [1-8]=token ASCII
        val tokenBytes = USB_TOKEN.toByteArray(Charsets.US_ASCII)
        val payload = ByteArray(9)
        payload[0] = route.toByte()
        System.arraycopy(tokenBytes, 0, payload, 1, minOf(8, tokenBytes.size))

        val packet = Packet(PacketType.HELLO, LINK_TYPE_ID, 0.toUShort(), payload)
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
