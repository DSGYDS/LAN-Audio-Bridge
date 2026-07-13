package com.lanbridge.app.core.impl

import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.interfaces.ITransport

class NullTransport : ITransport {
    override suspend fun connect() {}
    override suspend fun disconnect() {}
    override suspend fun send(data: ByteArray) {}
    override var onPacketReceived: ((ByteArray) -> Unit)? = null
    override val isConnected: Boolean = false
    override val type: TransportType = TransportType.Udp
}
