using System;
using LanAudioBridge.Core.Adapters;

namespace LanAudioBridge.Core.Factory;

/// <summary>
/// PlatformFactory — 平台工厂（第一批工厂方法）
///
/// P3 实现三个核心工厂方法：
///   CreateLogger     — 返回 ILogger 实现
///   CreateTransport  — 按类型构造 ITransport
///   CreateProtocol   — 返回 IPacketProtocol 实现
/// </summary>
public static class PlatformFactory
{
    /// <summary>创建平台日志实现（默认 ConsoleLogger）</summary>
    public static ILogger CreateLogger() => new ConsoleLogger();

    /// <summary>
    /// 创建传输层实例。
    /// 当前所有链路共用 UDP 传输，后续链路分离后由各链路文件提供专属工厂方法。
    /// </summary>
    public static ITransport CreateTransport(TransportType type, string? host = null, int port = 12345)
    {
        if (type != TransportType.Udp)
            throw new System.ArgumentOutOfRangeException(nameof(type), $"Unsupported transport: {type}");
        return new UdpTransport(port, host, port);
    }

    /// <summary>创建协议编解码实例</summary>
    public static IPacketProtocol CreateProtocol() => new PacketHeaderAdapter();
}

