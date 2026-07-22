using System;
using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// PacketHeaderAdapter — IPacketProtocol 的 PacketHeader 实现
///
/// 将现有的静态 PacketHeader 编解码包装为接口形式，
/// 使业务层可以通过 IPacketProtocol 依赖注入，不直接耦合 PacketHeader。
/// </summary>
public sealed class PacketHeaderAdapter : IPacketProtocol
{
    public byte[] Encode(in Packet packet)
    {
        var header = PacketHeader.EncodeHeader(
            (byte)packet.Type,
            packet.LinkType,
            packet.Sequence,
            packet.Payload.Length);

        var result = new byte[header.Length + packet.Payload.Length];
        header.CopyTo(result, 0);
        Buffer.BlockCopy(packet.Payload, 0, result, header.Length, packet.Payload.Length);
        return result;
    }

    public Packet? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < PacketHeader.HeaderSize)
            return null;

        var info = PacketHeader.TryDecode(data);
        if (info == null)
            return null;

        var payload = data[PacketHeader.HeaderSize..].ToArray();
        return new Packet
        {
            Type = (PacketType)info.Value.Type,
            LinkType = info.Value.LinkType,
            Sequence = unchecked((ushort)info.Value.Seq),
            Payload = payload
        };
    }
}
