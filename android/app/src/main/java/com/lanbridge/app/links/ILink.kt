package com.lanbridge.app.links

import android.media.projection.MediaProjection

/**
 * ILink — 统一链路接口
 *
 * 所有链路（WiFi LAN / WiFi Direct / Bluetooth / USB）实现此接口。
 * LinkManager 通过 when(linkType) 分发，不关心具体实现。
 */
interface ILink {
    /** 链路是否正在推流 */
    val isStreaming: Boolean

    /** 状态回调（LinkManager 统一订阅） */
    var onStatusChanged: ((String) -> Unit)?
    var onStreamingChanged: ((Boolean) -> Unit)?

    /** 连接并推流 */
    suspend fun connect(params: LinkParams): Boolean

    /** 推流中热切路由 */
    suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean

    /** 断开 */
    fun disconnect()
}

/**
 * 连接参数（统一入口，各链路取自己需要的字段）
 */
data class LinkParams(
    val host: String? = null,          // LAN: 目标 IP
    val token: String? = null,         // P2P: QR 码 token
    val route: Int = 0,                // 路线 0-3
    val proj: MediaProjection? = null  // 系统音频授权
)
