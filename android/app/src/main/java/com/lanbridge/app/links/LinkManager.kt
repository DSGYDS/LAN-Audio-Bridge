package com.lanbridge.app.links

import android.content.Context
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.links.wifidirect.WifiDirectLink
import com.lanbridge.app.links.wifilan.WifiLanLink
import com.lanbridge.app.net.LinkType

/**
 * LinkManager — 纯路由器（~50 行）
 *
 * 只做一件事：when(linkType) 分发到对应链路。
 * 不含任何链路实现代码。
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

    // ── 统一入口 ──

    suspend fun connect(linkType: Byte, params: LinkParams): Boolean {
        val link: ILink = when (linkType) {
            LinkType.WIFI_LAN -> wifiLan
            LinkType.WIFI_DIRECT -> wifiDirect
            else -> return false
        }
        activeLink = link
        return link.connect(params)
    }

    suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        return activeLink?.sendRouteUpdate(route, proj) ?: false
    }

    fun disconnect() {
        activeLink?.disconnect()
        activeLink = null
    }

    // ── 生命周期 ──

    fun start() = wifiLan.start()
    fun stop() = wifiLan.stop()

    // ── 状态查询 ──

    val isStreaming: Boolean get() = activeLink?.isStreaming ?: false
    fun routeToCapture(r: Int) = wifiLan.routeToCapture(r)
}
