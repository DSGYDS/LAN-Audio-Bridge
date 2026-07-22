package com.lanbridge.app.core.adapters

import com.lanbridge.app.core.interfaces.IPacketProtocol
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.net.PacketHeader
import com.lanbridge.app.net.PacketType

/**
 * PacketHeaderAdapter — IPacketProtocol 的 PacketHeader 实现
 *
 * 将现有的静态 PacketHeader 编解码包装为接口形式。
 */
class PacketHeaderAdapter : IPacketProtocol {
    override fun encode(packet: Packet): ByteArray {
        val header = PacketHeader.encodeHeader(
            packet.type.code,
            packet.linkType,
            packet.sequence.toInt(),
            packet.payload.size
        )
        return header + packet.payload
    }

    override fun decode(data: ByteArray): Packet? {
        if (data.size < PacketHeader.HEADER_SIZE) return null

        val info = PacketHeader.tryDecode(data) ?: return null

        val payload = data.copyOfRange(
            PacketHeader.HEADER_SIZE, data.size
        )
        return Packet(
            type = PacketType.fromCode(info.type) ?: return null,
            linkType = info.linkType,
            sequence = info.seq.toUShort(),
            payload = payload
        )
    }
}
