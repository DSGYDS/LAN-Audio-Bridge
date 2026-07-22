package com.lanbridge.app.core.interfaces

import com.lanbridge.app.core.enums.NetworkQuality
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.models.NetworkInfo

/**
 * INetworkMonitor — 统一网络状态监听接口
 *
 * 职责仅限"网络状态监听"，不含重连逻辑。
 * 重连逻辑由 ReconnectionManager 基于此接口的回调触发。
 *
 * 当前实现：
 *   Android — AndroidNetworkMonitor（基于 ConnectivityManager.NetworkCallback）
 *   Windows — WinNetworkMonitor（基于 NetworkChange）
 */
interface INetworkMonitor {
    /** 开始监听网络状态变化 */
    fun start()

    /** 停止监听 */
    fun stop()

    /** 当前是否有网络连接 */
    val isConnected: Boolean

    /** 当前活跃的传输类型 */
    val activeTransport: TransportType

    /** 当前网络质量 */
    val quality: NetworkQuality

    /** 网络状态变化时回调 */
    var onNetworkChanged: ((NetworkInfo) -> Unit)?
}
