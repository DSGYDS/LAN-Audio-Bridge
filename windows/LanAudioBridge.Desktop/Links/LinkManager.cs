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
    private readonly BluetoothLink _bluetooth;
    private readonly UsbLink _usb;
    private readonly ConnectionStateManager _stateManager = new();

    // ── 公开属性 ──
    public WifiLanLink WifiLan => _wifiLan;
    public WifiDirectLink WifiDirect => _wifiDirect;
    public BluetoothLink Bluetooth => _bluetooth;
    public UsbLink Usb => _usb;
    public ConnectionStateManager StateManager => _stateManager;

    public float Volume
    {
        get => _wifiLan.Engine.Volume;
        set => _wifiLan.Engine.Volume = value;
    }

    public LinkManager()
    {
        _wifiLan = new WifiLanLink(_stateManager);
        _wifiDirect = new WifiDirectLink(_stateManager, HandleRoute);
        _bluetooth = new BluetoothLink(_stateManager);
        _usb = new UsbLink(_stateManager);

        // 蓝牙会话开始时暂停 LAN 引擎（避免看门狗冲突），结束时恢复
        _bluetooth.OnSessionStarted += () => _wifiLan.Engine.Stop();
        _bluetooth.OnSessionEnded += () => _wifiLan.Engine.Start();

        // USB 会话同理
        _usb.OnSessionStarted += () => _wifiLan.Engine.Stop();
        _usb.OnSessionEnded += () => _wifiLan.Engine.Start();
    }

    /// <summary>共享路由控制 — 任何链路握手成功后都通过这里设置 AudioRouter 模式</summary>
    private bool HandleRoute(int route) => _wifiLan.HandleRoute(route);

    // ── 操作转发 ──

    /// <summary>启动 LAN 常驻服务</summary>
    public Task StartLanAsync() => _wifiLan.StartAsync();

    /// <summary>启动 P2P 链路</summary>
    public Task StartP2pAsync() => _wifiDirect.StartAsync();

    /// <summary>停止 P2P 链路</summary>
    public Task StopP2pAsync() => _wifiDirect.StopAsync();

    /// <summary>启动蓝牙链路（常驻监听，与 LAN 一起启动）</summary>
    public Task StartBluetoothAsync() => _bluetooth.StartAsync();

    /// <summary>停止蓝牙链路</summary>
    public Task StopBluetoothAsync() => _bluetooth.StopAsync();

    /// <summary>启动 USB 链路（用户点击触发）</summary>
    public Task StartUsbAsync() => _usb.StartAsync();

    /// <summary>停止 USB 链路</summary>
    public Task StopUsbAsync() => _usb.StopAsync();

    public bool IsP2pActive => _wifiDirect.IsActive;
    public bool IsBluetoothActive => _bluetooth.IsActive;
    public bool IsUsbActive => _usb.IsActive;

    public void Dispose()
    {
        _wifiLan.Dispose();
        _wifiDirect.Dispose();
        _bluetooth.Dispose();
        _usb.Dispose();
    }
}
