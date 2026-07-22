namespace LanAudioBridge.Core;

/// <summary>
/// 网络状态信息快照
/// </summary>
public struct NetworkInfo
{
    /// <summary>是否已连接</summary>
    public bool IsConnected;

    /// <summary>当前活跃的传输类型</summary>
    public TransportType TransportType;

    /// <summary>网络质量</summary>
    public NetworkQuality Quality;

    /// <summary>WiFi SSID（如有）</summary>
    public string? Ssid;

    /// <summary>网络接口名</summary>
    public string? InterfaceName;
}
