using System;

namespace LanAudioBridge.Core;

/// <summary>
/// IDiscovery — 统一设备发现接口（发现方视角）
///
/// 当前实现：
///   Android — NsdDiscoveryAdapter（NsdManager 扫描 _lan-audio._udp）
///   Windows — WinDiscovery（桩实现，Windows 当前是被发现方，不主动发现）
///
/// 设计说明：
///   Windows 端的 mDNS 发布功能保留在 MdnsPublisher 中，
///   此接口只承诺"发现"能力。以后 Windows 也需要发现设备时再实现。
/// </summary>
public interface IDiscovery
{
    /// <summary>开始发现</summary>
    void Start();

    /// <summary>停止发现</summary>
    void Stop();

    /// <summary>发现新设备时触发</summary>
    event Action<DeviceInfo>? OnDeviceFound;

    /// <summary>设备丢失时触发</summary>
    event Action<DeviceInfo>? OnDeviceLost;
}
