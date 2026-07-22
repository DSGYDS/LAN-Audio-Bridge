using System;
using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core;

/// <summary>
/// Packet — 协议数据包
/// </summary>
public struct Packet
{
    public PacketType Type;
    public byte LinkType;
    public ushort Sequence;
    public byte[] Payload;
}

/// <summary>
/// IPacketProtocol — 统一协议编解码接口
///
/// 当前由 PacketHeaderAdapter 包裹 PacketHeader 实现。
/// 以后协议版本升级（V2/V3）只改此接口的实现。
/// </summary>
public interface IPacketProtocol
{
    byte[] Encode(in Packet packet);
    Packet? Decode(ReadOnlySpan<byte> data);
}
