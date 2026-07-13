package com.lanbridge.app.core.interfaces

import com.lanbridge.app.net.PacketType

data class Packet(
    val type: PacketType,
    val sequence: UShort,
    val payload: ByteArray
)

interface IPacketProtocol {
    fun encode(packet: Packet): ByteArray
    fun decode(data: ByteArray): Packet?
}
