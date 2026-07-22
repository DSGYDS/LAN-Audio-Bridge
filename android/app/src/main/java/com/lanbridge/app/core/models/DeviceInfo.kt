package com.lanbridge.app.core.models

import com.lanbridge.app.core.enums.TransportType

/**
 * 发现的设备信息
 */
data class DeviceInfo(
    /** 设备名称 */
    val name: String,
    /** IP 地址 */
    val ip: String,
    /** 服务端口 */
    val port: Int,
    /** 传输类型 */
    val transport: TransportType = TransportType.Udp
)
