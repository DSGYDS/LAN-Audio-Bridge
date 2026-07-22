package com.lanbridge.app.core.factory

import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.impl.LogcatLogger
import com.lanbridge.app.core.interfaces.ILogger
import com.lanbridge.app.core.interfaces.IPacketProtocol
import com.lanbridge.app.core.interfaces.ITransport

/**
 * PlatformFactory — 平台工厂（第一批工厂方法）
 */
object PlatformFactory {
    fun createLogger(): ILogger = LogcatLogger()

    /**
     * 创建传输层实例。
     * 当前所有链路共用 UDP 传输，后续链路分离后由各链路文件提供专属工厂方法。
     *
     * @param type 传输类型
     * @param host 远程主机（null = server 模式）
     * @param port 端口（server 模式为绑定端口，client 模式为远程端口）
     * @param localPort 本地绑定端口（0 = 随机，仅 client 模式）
     * @param localBindAddress 本地绑定地址（P2P 模式绑定到 P2P 接口 IP）
     */
    fun createTransport(
        type: TransportType,
        host: String? = null,
        port: Int = 12345,
        localPort: Int = 0,
        localBindAddress: String? = null
    ): ITransport {
        return when (type) {
            TransportType.Udp -> UdpTransport(
                localPort = if (host != null) localPort else port,
                remoteHost = host,
                remotePort = port,
                localBindAddress = localBindAddress
            )
        }
    }

    fun createProtocol(): IPacketProtocol = PacketHeaderAdapter()
}
