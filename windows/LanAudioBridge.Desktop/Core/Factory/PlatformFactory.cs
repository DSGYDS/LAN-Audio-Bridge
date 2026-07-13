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
    /// 按 TransportType 创建传输层实例。
    /// </summary>
    public static ITransport CreateTransport(TransportType type, string? host = null, int port = 12345)
    {
        return type switch
        {
            TransportType.Udp => new UdpTransport(port, host, port),
            TransportType.WifiDirect => new NullTransport(),
            _ => new NullTransport(),
        };
    }

    /// <summary>创建协议编解码实例</summary>
    public static IPacketProtocol CreateProtocol() => new PacketHeaderAdapter();
}

