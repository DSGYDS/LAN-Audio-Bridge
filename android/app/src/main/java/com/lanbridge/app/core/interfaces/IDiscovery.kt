package com.lanbridge.app.core.interfaces

import com.lanbridge.app.core.models.DeviceInfo

/**
 * IDiscovery — 统一设备发现接口（发现方视角）
 *
 * 当前实现：
 *   Android — NsdDiscoveryAdapter（NsdManager 扫描 _lan-audio._udp）
 *   Windows — WinDiscovery（桩实现，Windows 是被发现方）
 */
interface IDiscovery {
    /** 开始发现 */
    fun start()

    /** 停止发现 */
    fun stop()

    /** 发现新设备时回调 */
    var onDeviceFound: ((DeviceInfo) -> Unit)?

    /** 设备丢失时回调 */
    var onDeviceLost: ((DeviceInfo) -> Unit)?
}
