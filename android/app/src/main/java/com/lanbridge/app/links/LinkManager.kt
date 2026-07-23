package com.lanbridge.app.links

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.links.wifidirect.WifiDirectLink
import com.lanbridge.app.links.wifilan.WifiLanLink
import com.lanbridge.app.net.LinkType

/**
 * LinkManager — 纯路由器 + 单链路互斥
 *
 * 职责：
 * 1. when(linkType) 分发到对应链路
 * 2. 强制同一时刻只有一条链路活跃（connect 前自动 disconnect 旧链路）
 * 3. 不含任何链路实现代码，不引用链路内部逻辑
 */
class LinkManager(
    context: Context,
    pipe: AudioPipeline,
    val stateManager: ConnectionStateManager
) {
    // ── 四级链路实例 ──
    val wifiLan = WifiLanLink(context, pipe, stateManager)
    val wifiDirect = WifiDirectLink(context, pipe, stateManager)
    // val bluetooth = BluetoothLink(context, pipe, stateManager)  // 预留
    // val usb = UsbLink(context, pipe, stateManager)              // 预留

    private var activeLink: ILink? = null

    /** 上一次活跃的链路类型（用于 UI 显示当前链路） */
    var lastLinkType: Byte = LinkType.WIFI_LAN
        private set

    // ── 统一入口（单链路互斥） ──

    /**
     * 连接指定链路。如果当前有其他链路活跃，先断开旧链路再连接新链路。
     * 四条链路互不知道对方存在，切换对链路透明。
     */
    suspend fun connect(linkType: Byte, params: LinkParams): Boolean {
        val link: ILink = when (linkType) {
            LinkType.WIFI_LAN -> wifiLan
            LinkType.WIFI_DIRECT -> wifiDirect
            else -> return false
        }

        // 单链路互斥：断开旧链路
        if (activeLink != null && activeLink !== link) {
            activeLink?.disconnect()
        }

        activeLink = link
        lastLinkType = linkType
        return link.connect(params)
    }

    /**
     * 重连：沿用上一次的链路类型，参数由调用方传入。
     * LinkManager 不关心链路内部如何获取 token/host。
     */
    suspend fun reconnect(params: LinkParams): Boolean {
        return connect(lastLinkType, params)
    }

    suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        return activeLink?.sendRouteUpdate(route, proj) ?: false
    }

    fun disconnect() {
        activeLink?.disconnect()
        activeLink = null
    }

    // ── 状态查询 ──

    val isStreaming: Boolean get() = activeLink?.isStreaming ?: false

    companion object {
        /** 路线编号 → 采集模式（共享工具，不属于任何链路） */
        fun routeToCapture(r: Int): Int = when (r) {
            0, 3 -> AudioPipeline.MODE_SYSTEM
            1 -> AudioPipeline.MODE_MIX
            else -> AudioPipeline.MODE_MIC
        }
    }
}
