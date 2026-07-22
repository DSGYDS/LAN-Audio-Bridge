namespace LanAudioBridge.Core;

/// <summary>
/// 发现的设备信息
/// </summary>
public struct DeviceInfo
{
    /// <summary>设备名称</summary>
    public string Name;

    /// <summary>IP 地址</summary>
    public string Ip;

    /// <summary>服务端口</summary>
    public int Port;

    /// <summary>传输类型</summary>
    public TransportType Transport;
}
