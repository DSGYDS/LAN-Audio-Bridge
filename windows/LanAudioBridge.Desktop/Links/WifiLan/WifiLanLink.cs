namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// WiFi LAN 链路常量 — 同局域网 WiFi 直连
///
/// 角色：Windows 做接收端（server），Android 做发送端（client）
/// 发现：mDNS（_lan-audio._udp）
/// 握手：Android → Windows HELLO，Windows 回 HELLO_ACK
/// 传输：UDP 单播
/// </summary>
public static class WifiLanLink
{
    /// <summary>链路类型标识（包头 [6] 字段）</summary>
    public const byte LinkTypeId = 0x01;

    /// <summary>音频数据端口</summary>
    public const int AudioPort = 12345;

    /// <summary>握手/控制信令端口</summary>
    public const int HandshakePort = 12347;

    /// <summary>mDNS 服务类型</summary>
    public const string MdnsServiceType = "_lan-audio._udp";
}
