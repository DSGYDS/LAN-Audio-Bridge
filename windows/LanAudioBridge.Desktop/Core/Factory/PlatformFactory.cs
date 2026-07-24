using System;
using LanAudioBridge.Core.Adapters;

namespace LanAudioBridge.Core.Factory;

/// <summary>
/// PlatformFactory — 平台工厂
///
/// 第一批（P3 已实现）：CreateLogger / CreateTransport / CreateProtocol
/// 第二批（P3 新增）：CreateCapturer / CreateRenderer / CreateDiscovery / CreateNetworkMonitor
/// </summary>
public static class PlatformFactory
{
    // ── 第一批工厂方法（P3 已实现） ──

    /// <summary>创建平台日志实现（默认 ConsoleLogger）</summary>
    public static ILogger CreateLogger() => new ConsoleLogger();

    /// <summary>
    /// 创建传输层实例。
    /// 当前所有链路共用 UDP 传输，后续链路分离后由各链路文件提供专属工厂方法。
    /// </summary>
    /// <param name="type">传输类型</param>
    /// <param name="host">远程主机（null = server 模式）</param>
    /// <param name="port">端口（server 模式为绑定端口，client 模式为远程端口）</param>
    /// <param name="localPort">本地绑定端口（0 = 随机，仅 client 模式）</param>
    public static ITransport CreateTransport(TransportType type, string? host = null, int port = 12345, int localPort = 0)
    {
        return type switch
        {
            TransportType.Udp => new UdpTransport(host != null ? localPort : port, host, port),
            // Bluetooth 链路由 BluetoothLink 直接创建 BluetoothTransport（不走 host/port 模式）
            TransportType.Bluetooth => throw new System.InvalidOperationException(
                "BluetoothTransport must be created by BluetoothLink (requires RfcommDeviceService)"),
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), $"Unsupported transport: {type}")
        };
    }

    /// <summary>创建协议编解码实例</summary>
    public static IPacketProtocol CreateProtocol() => new PacketHeaderAdapter();

    // ── 第二批工厂方法（P3 新增） ──

    /// <summary>
    /// 创建音频采集器。
    /// Windows 端为桩实现（Windows 是纯接收端，不采集音频）。
    /// </summary>
    public static IAudioCapturer CreateCapturer(CapturerType type = CapturerType.Microphone)
        => new StubCapturer();

    /// <summary>
    /// 创建音频渲染器。
    /// </summary>
    /// <param name="useCable">true = CABLE Input（虚拟麦克风），false = 扬声器</param>
    public static IAudioRenderer CreateRenderer(bool useCable = false)
        => useCable ? new CableRenderer() : new SpeakerRenderer();

    /// <summary>
    /// 创建设备发现实例。
    /// Windows 端为桩实现（Windows 是被发现方，不主动发现）。
    /// </summary>
    public static IDiscovery CreateDiscovery() => new WinDiscovery();

    /// <summary>创建网络状态监听实例</summary>
    public static INetworkMonitor CreateNetworkMonitor() => new WinNetworkMonitor();
}

