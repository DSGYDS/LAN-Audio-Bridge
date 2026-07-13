package com.lanbridge.app.core.factory

import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.adapters.UdpTransport
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.impl.LogcatLogger
import com.lanbridge.app.core.impl.NullTransport
import com.lanbridge.app.core.interfaces.ILogger
import com.lanbridge.app.core.interfaces.IPacketProtocol
import com.lanbridge.app.core.interfaces.ITransport

/**
 * PlatformFactory — 平台工厂（第一批工厂方法）
 */
object PlatformFactory {
    fun createLogger(): ILogger = LogcatLogger()

    fun createTransport(type: TransportType, host: String? = null, port: Int = 12345): ITransport {
        return when (type) {
            TransportType.Udp -> UdpTransport(port, host, port)
            TransportType.WifiDirect -> NullTransport()
            else -> NullTransport()
        }
    }

    fun createProtocol(): IPacketProtocol = PacketHeaderAdapter()
}
