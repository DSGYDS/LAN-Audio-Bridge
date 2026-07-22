package com.lanbridge.app.core.models

import com.lanbridge.app.core.enums.NetworkQuality
import com.lanbridge.app.core.enums.TransportType

/**
 * 网络状态信息快照
 */
data class NetworkInfo(
    /** 是否已连接 */
    val isConnected: Boolean,
    /** 当前活跃的传输类型 */
    val transportType: TransportType = TransportType.Udp,
    /** 网络质量 */
    val quality: NetworkQuality = NetworkQuality.Unknown,
    /** WiFi SSID（如有） */
    val ssid: String? = null,
    /** 网络接口名 */
    val interfaceName: String? = null
)
