using System;
using System.Threading.Tasks;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// ILink — 统一链路接口
///
/// 所有链路（WiFi LAN / WiFi Direct / Bluetooth / USB）实现此接口。
/// LinkManager 通过 linkType 分发，不关心具体实现。
/// </summary>
public interface ILink : IDisposable
{
    /// <summary>链路是否活跃</summary>
    bool IsActive { get; }

    /// <summary>启动链路</summary>
    Task StartAsync();

    /// <summary>停止链路</summary>
    Task StopAsync();
}
