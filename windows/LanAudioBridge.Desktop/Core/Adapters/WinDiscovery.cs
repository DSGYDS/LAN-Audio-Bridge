using System;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// WinDiscovery — Windows 设备发现桩实现
///
/// Windows 当前是被发现方（通过 MdnsPublisher 发布 mDNS 服务），
/// 不主动发现其他设备。此桩实现满足接口完整性。
///
/// 以后 Windows 也需要发现设备时（如多电脑场景），再实现真正的发现逻辑。
/// </summary>
public sealed class WinDiscovery : IDiscovery
{
    private const string Tag = "WinDiscovery";

    public event Action<DeviceInfo>? OnDeviceFound;
    public event Action<DeviceInfo>? OnDeviceLost;

    public void Start()
    {
        Log.I(Tag, "WinDiscovery started (stub - Windows is the discovered party)");
    }

    public void Stop()
    {
        Log.I(Tag, "WinDiscovery stopped");
    }
}
