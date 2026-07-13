package com.lanbridge.app.core.adapters

import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.ITransport
import kotlinx.coroutines.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress

/**
 * UdpTransport — ITransport 的 UDP 实现
 *
 * 包裹 DatagramSocket，支持 server 模式（仅绑定端口）和 client 模式（指定远程主机）。
 */
class UdpTransport(
    private val localPort: Int,
    private val remoteHost: String? = null,
    private val remotePort: Int? = null
) : ITransport {
    private val socket = DatagramSocket(localPort)
    private var _isConnected = false
    private var scope: CoroutineScope? = null
    private val recvBuf = ByteArray(65507) // 最大 UDP 包

    override suspend fun connect() {
        if (_isConnected) return

        if (remoteHost != null) {
            socket.connect(InetSocketAddress(remoteHost, remotePort ?: localPort))
        }
        _isConnected = true
        scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        scope!!.launch { receiveLoop() }
    }

    override suspend fun disconnect() {
        _isConnected = false
        scope?.cancel()
        scope = null
    }

    override suspend fun send(data: ByteArray) {
        try {
            if (remoteHost != null) {
                // client 模式：connect 后直接 send
                socket.send(DatagramPacket(data, data.size))
            } else {
                Log.w("UdpTransport", "send called in server mode without remote address")
            }
        } catch (e: Exception) {
            Log.e("UdpTransport", "send error: ${e.message}")
        }
    }

    /** 向指定地址发送（server 模式使用） */
    suspend fun sendTo(data: ByteArray, addr: InetSocketAddress) {
        socket.send(DatagramPacket(data, data.size, addr))
    }

    private suspend fun receiveLoop() = withContext(Dispatchers.IO) {
        while (isActive && _isConnected) {
            try {
                val packet = DatagramPacket(recvBuf, recvBuf.size)
                socket.receive(packet)
                val bytes = packet.data.copyOfRange(0, packet.length)
                onPacketReceived?.invoke(bytes)
            } catch (e: CancellationException) { break }
            catch (e: Exception) {
                Log.e("UdpTransport", "receiveLoop error: ${e.message}")
            }
        }
    }

    override var onPacketReceived: ((ByteArray) -> Unit)? = null
    override val isConnected: Boolean get() = _isConnected
    override val type: TransportType = TransportType.Udp
}
