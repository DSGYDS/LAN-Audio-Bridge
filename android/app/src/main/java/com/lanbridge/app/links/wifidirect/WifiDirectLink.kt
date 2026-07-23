package com.lanbridge.app.links.wifidirect

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionState
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.StreamingService
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.links.ILink
import com.lanbridge.app.links.LinkParams
import com.lanbridge.app.net.HandshakeManager
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.P2pPairStore
import com.lanbridge.app.net.WifiDirectManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * WifiDirectLink — WiFi Direct P2P 链路（完整实现）
 *
 * 职责：扫码 → createGroup → 等待 Windows HELLO → 推流。
 * 与 LAN / 蓝牙 / USB 完全解耦。
 */
class WifiDirectLink(
    private val context: Context,
    private val pipe: AudioPipeline,
    private val stateManager: ConnectionStateManager
) : ILink {

    companion object {
        /** 链路类型标识（包头 [6] 字段） */
        const val LINK_TYPE_ID: Byte = 0x02
        const val AUDIO_PORT = 12345
        const val HANDSHAKE_PORT = 12347
        const val HANDSHAKE_TIMEOUT_MS = 3000L
        const val HANDSHAKE_MAX_ATTEMPTS = 3
        const val IP_POLL_INTERVAL_MS = 500L
        const val IP_POLL_MAX_RETRIES = 16
    }

    // ── 子模块 ──
    val wifiDirectManager = WifiDirectManager(context)
    private val connectMutex = Mutex()  // 防止并发 connect（扫码过快时两次调用冲突）

    // ── ILink 状态 ──
    @Volatile override var isStreaming = false
        private set
    override var onStatusChanged: ((String) -> Unit)? = null
    override var onStreamingChanged: ((Boolean) -> Unit)? = null

    // ── P2P 特有状态 ──
    @Volatile var p2pTargetIp: String? = null
        private set
    @Volatile var currentRoute: Int = 0

    // ── ILink 实现 ──

    override suspend fun connect(params: LinkParams): Boolean = connectMutex.withLock {
        // token 来源：扫码传入 或 已配对存储（冷启动免扫码）
        val token = params.token
            ?: P2pPairStore.load(context)?.token
            ?: return@withLock false
        currentRoute = params.route

        onStatusChanged?.invoke("正在创建 P2P Group...")
        val goIp = wifiDirectManager.createGroupAndWaitForClient()
        if (goIp == null) {
            onStatusChanged?.invoke("P2P Group 创建失败")
            stateManager.update(ConnectionState.ERROR)
            return@withLock false
        }

        stateManager.update(ConnectionState.CONNECTING)

        // 重连策略：已知 Windows IP 时主动发 HELLO，否则被动等待
        val knownWinIp = HandshakeManager.lastRemoteIp
        val handshakeOk = if (knownWinIp != null) {
            onStatusChanged?.invoke("P2P 重连中，主动握手...")
            withContext(Dispatchers.IO) {
                HandshakeManager.handshake(knownWinIp, params.route, token, LinkType.WIFI_DIRECT)
            }
        } else {
            onStatusChanged?.invoke("P2P GO 就绪 ($goIp)，等待电脑连接...")
            withContext(Dispatchers.IO) {
                HandshakeManager.waitForHello(token, params.route)
            }
        }
        if (!handshakeOk) {
            onStatusChanged?.invoke("P2P 握手失败（电脑未响应）")
            stateManager.update(ConnectionState.ERROR)
            return@withLock false
        }

        stateManager.update(ConnectionState.CONNECTED)
        onStatusChanged?.invoke("P2P 握手成功 ✓ 准备推流")

        // 配对持久化：握手成功即写入，后续冷启动免扫码
        P2pPairStore.save(context, token, params.deviceName ?: "")

        val winP2pIp = HandshakeManager.lastRemoteIp ?: goIp
        p2pTargetIp = winP2pIp
        val capMode = routeToCapture(params.route)

        val ok = withContext(Dispatchers.IO) {
            pipe.currentLinkType = LinkType.WIFI_DIRECT
            pipe.startStreaming(capMode, params.proj, context, winP2pIp)
        }

        if (ok) {
            isStreaming = true
            pipe.onFirstFrame = { stateManager.update(ConnectionState.STREAMING) }
            onStatusChanged?.invoke("P2P 推流中：路线${params.route + 1}")
            onStreamingChanged?.invoke(true)
            context.startForegroundService(Intent(context, StreamingService::class.java))
        } else {
            onStatusChanged?.invoke("P2P 启动推流失败")
            stateManager.update(ConnectionState.ERROR)
        }
        ok
    }

    override suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        if (!isStreaming) { currentRoute = route; return true }
        currentRoute = route

        val capMode = routeToCapture(route)
        val ok = withContext(Dispatchers.IO) { pipe.switchMode(capMode, proj, context) }
        if (!ok) { onStatusChanged?.invoke("需先授权系统音频"); return false }

        val targetIp = p2pTargetIp ?: return false
        withContext(Dispatchers.IO) { HandshakeManager.sendRouteUpdate(targetIp, route, LinkType.WIFI_DIRECT) }
        return true
    }

    override fun disconnect() {
        context.stopService(Intent(context, StreamingService::class.java))
        pipe.stopStreaming()
        isStreaming = false
        p2pTargetIp = null
        stateManager.update(ConnectionState.DISCONNECTED)
        onStatusChanged?.invoke("已停止")
        onStreamingChanged?.invoke(false)
    }

    // ── 工具 ──

    private fun routeToCapture(r: Int) = when (r) {
        0, 3 -> AudioPipeline.MODE_SYSTEM
        1 -> AudioPipeline.MODE_MIX
        else -> AudioPipeline.MODE_MIC
    }
}
