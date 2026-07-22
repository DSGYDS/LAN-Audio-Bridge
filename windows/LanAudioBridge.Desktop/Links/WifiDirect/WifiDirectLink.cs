namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// WiFi Direct 链路常量 — P2P 直连（Android 做 GO，Windows 做客户端）
///
/// 角色反转：Android 做 Group Owner（自带 DHCP），Windows 做客户端
/// 发现：Windows 轮询 DeviceInformation.FindAllAsync（WiFiDirectDevice）
/// 握手：Windows → Android HELLO（携带 token），Android 回 HELLO_ACK
/// 传输：UDP 单播（P2P 适配器 IP）
/// </summary>
public static class WifiDirectLink
{
    /// <summary>链路类型标识（包头 [6] 字段）</summary>
    public const byte LinkTypeId = 0x02;

    /// <summary>音频数据端口（与 LAN 共用端口号，但走不同网络接口）</summary>
    public const int AudioPort = 12345;

    /// <summary>握手/控制信令端口</summary>
    public const int HandshakePort = 12347;

    /// <summary>Android GO 固定 IP</summary>
    public const string GoIp = "192.168.49.1";

    /// <summary>P2P 设备发现轮询间隔（ms）</summary>
    public const int DiscoverIntervalMs = 3000;

    /// <summary>P2P 设备发现超时（ms）</summary>
    public const int DiscoverTimeoutMs = 120_000;

    /// <summary>P2P 适配器 IP 轮询间隔（ms）</summary>
    public const int IpPollIntervalMs = 500;

    /// <summary>P2P 适配器 IP 轮询最大次数</summary>
    public const int IpPollMaxRetries = 30;
}
