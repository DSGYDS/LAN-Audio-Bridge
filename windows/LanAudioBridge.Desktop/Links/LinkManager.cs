using System;
using System.Threading.Tasks;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// LinkManager — 纯路由器（~50 行）
///
/// 只做一件事：持有各链路实例，转发操作。
/// 不含任何链路实现代码。
/// </summary>
public sealed class LinkManager : IDisposable
{
    // ── 四级链路实例 ──
    private readonly WifiLanLink _wifiLan;
    private readonly WifiDirectLink _wifiDirect;
    private readonly ConnectionStateManager _stateManager = new();

    // ── 公开属性 ──
    public WifiLanLink WifiLan => _wifiLan;
    public WifiDirectLink WifiDirect => _wifiDirect;
    public ConnectionStateManager StateManager => _stateManager;

    public float Volume
    {
        get => _wifiLan.Volume;
        set => _wifiLan.Volume = value;
    }

    public LinkManager()
    {
        _wifiLan = new WifiLanLink(_stateManager);
        _wifiDirect = new WifiDirectLink(
            _wifiLan.HandshakeServer, _stateManager, _wifiLan.HandleRoute);
    }

    // ── 操作转发 ──

    /// <summary>启动 LAN 常驻服务</summary>
    public Task StartLanAsync() => _wifiLan.StartAsync();

    /// <summary>启动 P2P 链路</summary>
    public Task StartP2pAsync() => _wifiDirect.StartAsync();

    /// <summary>停止 P2P 链路</summary>
    public Task StopP2pAsync() => _wifiDirect.StopAsync();

    public bool IsP2pActive => _wifiDirect.IsActive;

    public void Dispose()
    {
        _wifiLan.Dispose();
        _wifiDirect.Dispose();
    }
}
