package com.lanbridge.app.core.interfaces

import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType

data class Packet(
    val type: PacketType,
    val linkType: Byte = LinkType.UNKNOWN,
    val sequence: UShort,
    val payload: ByteArray
)

interface IPacketProtocol {
    fun encode(packet: Packet): ByteArray
    fun decode(data: ByteArray): Packet?
}
