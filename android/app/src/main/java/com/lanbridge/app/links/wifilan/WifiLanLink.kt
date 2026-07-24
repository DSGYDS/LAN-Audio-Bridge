package com.lanbridge.app.links.wifilan

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionState
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.StreamingService
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.core.factory.PlatformFactory
import com.lanbridge.app.links.ILink
import com.lanbridge.app.links.LinkParams
import com.lanbridge.app.net.HandshakeManager
import com.lanbridge.app.net.LanAudioDiscovery
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.ReconnectionManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * WifiLanLink — WiFi LAN 链路（完整实现）
 *
 * 职责：mDNS 发现 + 握手 + 推流 + 断线重连。
 * 与 WiFi Direct / 蓝牙 / USB 完全解耦。
 *
 * 数据通路：AudioPipeline → EncodeSender → UdpTransport.sendBlocking() → Windows UDP 12345
 * 握手方向：Android 发 HELLO(route) → Windows 回 HELLO_ACK（与蓝牙一致）
 * 发现机制：NsdManager 扫描 _lan-audio._udp 服务
 * 重连机制：ReconnectionManager 监听网络状态，断线后自动重试
 */
class WifiLanLink(
    private val context: Context,
    private val pipe: AudioPipeline,
    private val stateManager: ConnectionStateManager
) : ILink {

    companion object {
        /** 链路类型标识（包头 [6] 字段） */
        const val LINK_TYPE_ID: Byte = 0x01
        const val AUDIO_PORT = 12345
        const val HANDSHAKE_PORT = 12347
        const val MDNS_SERVICE_TYPE = "_lan-audio._udp"
        const val HANDSHAKE_TIMEOUT_MS = 500L
    }

    // ── 子模块 ──
    private val discovery = LanAudioDiscovery(context)
    private val reconnectionManager: ReconnectionManager

    // ── ILink 状态 ──
    @Volatile override var isStreaming = false
        private set
    override var onStatusChanged: ((String) -> Unit)? = null
    override var onStreamingChanged: ((Boolean) -> Unit)? = null

    // ── LAN 特有回调 ──
    var onDeviceFound: ((LanAudioDiscovery.DeviceInfo) -> Unit)? = null
    var onDeviceLost: ((String) -> Unit)? = null

    @Volatile var currentTargetIp: String? = null
        private set
    @Volatile var currentRoute: Int = 0

    init {
        reconnectionManager = ReconnectionManager(
            context = context,
            stateManager = stateManager,
            pipeline = pipe,
            networkMonitor = PlatformFactory.createNetworkMonitor(context),
            onRecover = { host, mode ->
                val capMode = routeToCapture(mode)
                val ok = HandshakeManager.handshake(host, mode)
                if (!ok) return@ReconnectionManager false
                pipe.startStreaming(capMode, null, context, host)
            }
        )
    }

    // ── 生命周期（mDNS 发现 + 重连监听，App 启动时调用） ──

    /** 启动 mDNS 扫描 + 断线重连监听（常驻，与推流状态无关） */
    fun start() {
        reconnectionManager.start()
        discovery.setOnDeviceFound { device ->
            onDeviceFound?.invoke(device)
            if (!isStreaming) {
                onStatusChanged?.invoke("发现电脑：${device.name}")
                stateManager.update(ConnectionState.FOUND)
            }
        }
        discovery.setOnDeviceLost { name -> onDeviceLost?.invoke(name) }
        discovery.setOnError { msg -> if (!isStreaming) onStatusChanged?.invoke("设备发现：$msg") }
        discovery.startScan()
    }

    /** 停止 mDNS 扫描 + 重连监听 */
    fun stop() {
        reconnectionManager.stop()
        discovery.stopScan()
    }

    // ── ILink 实现 ──

    /**
     * 连接 LAN 链路（用户选择发现的电脑后触发）
     * 流程：发 HELLO 握手 → 启动推流 → 记录目标 IP（供重连用）
     */
    override suspend fun connect(params: LinkParams): Boolean {
        val host = params.host ?: return false
        currentRoute = params.route
        val capMode = routeToCapture(params.route)

        stateManager.update(ConnectionState.CONNECTING)
        onStatusChanged?.invoke("正在握手...")

        val handshakeOk = withContext(Dispatchers.IO) {
            HandshakeManager.handshake(host, params.route)
        }
        if (!handshakeOk) {
            stateManager.update(ConnectionState.ERROR)
            onStatusChanged?.invoke("握手失败")
            return false
        }

        stateManager.update(ConnectionState.CONNECTED)
        val ok = withContext(Dispatchers.IO) {
            pipe.currentLinkType = LinkType.WIFI_LAN
            pipe.startStreaming(capMode, params.proj, context, host)
        }

        if (ok) {
            isStreaming = true
            currentTargetIp = host
            reconnectionManager.lastKnownHost = host
            reconnectionManager.lastRouteMode = params.route
            pipe.onFirstFrame = { stateManager.update(ConnectionState.STREAMING) }
            onStatusChanged?.invoke("推流中：路线${params.route + 1} -> $host")
            onStreamingChanged?.invoke(true)
            context.startForegroundService(Intent(context, StreamingService::class.java))
        } else {
            stateManager.update(ConnectionState.ERROR)
            onStatusChanged?.invoke("启动推流失败")
        }
        return ok
    }

    /** 推流中热切路线：切换采集模式 + 通过 UDP 发送 ROUTE 包到 Windows */
    override suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        if (!isStreaming) { currentRoute = route; return true }
        currentRoute = route

        val capMode = routeToCapture(route)
        val ok = withContext(Dispatchers.IO) { pipe.switchMode(capMode, proj, context) }
        if (!ok) { onStatusChanged?.invoke("需先授权系统音频"); return false }

        val targetIp = currentTargetIp ?: return false
        withContext(Dispatchers.IO) { HandshakeManager.sendRouteUpdate(targetIp, route, LinkType.WIFI_LAN) }
        return true
    }

    /** 断开 LAN 链路：停止推流 + 清除重连记录 + 状态回退 */
    override fun disconnect() {
        context.stopService(Intent(context, StreamingService::class.java))
        pipe.stopStreaming()
        isStreaming = false
        currentTargetIp = null
        reconnectionManager.lastKnownHost = null
        stateManager.update(ConnectionState.DISCONNECTED)
        onStatusChanged?.invoke("已停止")
        onStreamingChanged?.invoke(false)
    }

    // ── 工具：路线编号 → 采集模式（与 LinkManager.routeToCapture 一致） ──

    fun routeToCapture(r: Int) = when (r) {
        0, 3 -> AudioPipeline.MODE_SYSTEM
        1 -> AudioPipeline.MODE_MIX
        else -> AudioPipeline.MODE_MIC
    }
}
