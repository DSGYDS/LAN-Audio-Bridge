package com.lanbridge.app.core.interfaces

import com.lanbridge.app.core.enums.TransportType

interface ITransport {
    suspend fun connect()
    suspend fun disconnect()
    suspend fun send(data: ByteArray)

    /** 收到数据包的回调 */
    var onPacketReceived: ((ByteArray) -> Unit)?

    val isConnected: Boolean
    val type: TransportType
}
