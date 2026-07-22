using System;

namespace LanAudioBridge.Desktop;

/// <summary>
/// PacketHeader — LAN Audio Bridge 通信协议通用包头。
///
/// 职责：仅负责 15B 包头的编解码，不含任何业务逻辑。
/// Sequence 的维护由发送方业务层负责。
///
/// 格式（15 字节，Version 0x02）：
/// [0-3]   Magic:        0x4C414242
/// [4]     Version:      0x02
/// [5]     Type:         包类型
/// [6]     LinkType:     链路类型（0x00=Unknown, 0x01=WiFi LAN, 0x02=WiFi Direct, 0x03=BT, 0x04=USB）
/// [7-10]  Sequence:     uint32 BE
/// [11-14] PayloadLength: uint32 BE
///
/// 设计约束：
/// - 无状态（Stateless）：不保存 Sequence、Socket 或任何运行时状态
/// - 纯工具类：只提供 Encode/Decode 静态方法
/// </summary>
public static class PacketHeader
{
    /// <summary>魔数 - 用于快速判断"这是我的协议包吗"</summary>
    public const int Magic = 0x4C414242;

    /// <summary>当前协议版本</summary>
    public const byte CurrentVersion = 0x02;

    /// <summary>包头固定长度</summary>
    public const int HeaderSize = 15;

    /// <summary>
    /// 编码包头（推荐形式）。
    /// Version 内部写死为 <see cref="CurrentVersion"/>，业务层无需关心。
    /// 返回 byte[15]，调用方自行在末尾拼接 Payload。
    /// </summary>
    public static byte[] EncodeHeader(byte type, byte linkType, uint seq, int payloadLen)
    {
        var buf = new byte[HeaderSize];
        // 0-3: Magic (大端序)
        WriteBigEndian32(buf, 0, Magic);
        // 4: Version
        buf[4] = CurrentVersion;
        // 5: Type
        buf[5] = type;
        // 6: LinkType
        buf[6] = linkType;
        // 7-10: Sequence (uint32 BE)
        WriteBigEndian32(buf, 7, unchecked((int)seq));
        // 11-14: PayloadLength (uint32 BE)
        WriteBigEndian32(buf, 11, payloadLen);
        return buf;
    }

    /// <summary>
    /// 解码包头。
    ///
    /// 校验规则（任一失败返回 null）：
    /// 1. 数据长度不足 14B
    /// 2. Magic 不匹配
    /// 3. Version 不匹配
    /// 4. PayloadLength 不等于 data.Length - HeaderSize
    ///
    /// 校验失败时调用方应丢弃整个包，不做截断或容错。
    /// </summary>
    public static PacketHeaderInfo? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) return null;

        // 校验 Magic
        int magic = ReadBigEndian32(data, 0);
        if (magic != Magic) return null;

        // 校验 Version
        byte version = data[4];
        if (version != CurrentVersion) return null;

        byte type = data[5];
        byte linkType = data[6];
        uint seq = unchecked((uint)ReadBigEndian32(data, 7));
        int payloadLen = ReadBigEndian32(data, 11);

        // 校验 PayloadLength
        int actualPayloadLen = data.Length - HeaderSize;
        if (payloadLen != actualPayloadLen) return null;

        return new PacketHeaderInfo(type, linkType, seq, payloadLen);
    }

    // ── 大端序读写工具（手动移位，平台无关） ──

    private static void WriteBigEndian32(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static int ReadBigEndian32(ReadOnlySpan<byte> buf, int offset)
    {
        return (buf[offset] << 24)
             | (buf[offset + 1] << 16)
             | (buf[offset + 2] << 8)
             | buf[offset + 3];
    }
}

/// <summary>包头解码结果（纯数据容器）</summary>
public readonly record struct PacketHeaderInfo(byte Type, byte LinkType, uint Seq, int PayloadLen);
