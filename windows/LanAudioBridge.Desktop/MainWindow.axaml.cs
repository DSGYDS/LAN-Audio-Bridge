using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using LanAudioBridge.Desktop.Links;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 主窗口 — 纯 UI 层
///
/// 职责：窗口生命周期 + UI 绑定 + 用户操作转发给 LinkManager。
/// 链路逻辑全部封装在 LinkManager 中，本文件不含任何链路代码。
/// </summary>
public partial class MainWindow : Window
{
    private readonly LinkManager _linkManager = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // 关闭时最小化到托盘，不退出
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    // ── 窗口加载完成 ──
    private void OnLoaded(object? sender, EventArgs e)
    {
        var lan = _linkManager.WifiLan;
        var p2p = _linkManager.WifiDirect;

        // ── 订阅 LAN 链路事件 → 更新 UI ──
        lan.OnStatusChanged += msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = msg);
        lan.OnStateChanged += state =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ConnectionStateText.Text = state.ToChineseLabel());

        // ── 订阅 P2P 链路事件 → 更新 UI ──
        p2p.OnP2pStatusChanged += msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => P2pStatusText.Text = msg);
        p2p.OnP2pProgressVisible += visible =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => P2pProgressPanel.IsVisible = visible);
        p2p.OnP2pProgress += (indeterminate, value) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                P2pProgressRing.IsIndeterminate = indeterminate;
                P2pProgressRing.Value = value;
            });
        p2p.OnQrChanged += (qrContent, deviceName) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (qrContent != null && deviceName != null)
                {
                    QrImage.Source = QrCodeHelper.Generate(qrContent);
                    QrImage.IsVisible = true;
                    P2pDeviceLabel.Text = $"设备：{deviceName}  请手机扫码";
                    P2pDeviceLabel.IsVisible = true;
                }
                else
                {
                    QrImage.IsVisible = false;
                    P2pDeviceLabel.IsVisible = false;
                }
            });
        p2p.OnP2pActiveChanged += active =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                P2pButton.Content = active ? "刷新二维码" : "启动 P2P");

        // ── 音量滑块 ──
        VolumeText.Text = $"{(int)(VolumeSlider.Value * 100)}%";
        VolumeSlider.ValueChanged += (_, _) =>
        {
            float vol = (float)VolumeSlider.Value;
            _linkManager.Volume = vol;
            VolumeText.Text = $"{(int)(vol * 100)}%";
        };

        // ── 订阅蓝牙链路事件 → 更新 UI ──
        var bt = _linkManager.Bluetooth;
        bt.OnStatusChanged += msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BtStatusText.Text = msg);
        bt.OnActiveChanged += active =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                BtButton.Content = active ? "停止蓝牙" : "启动蓝牙");

        // ── 订阅 USB 链路事件 → 更新 UI ──
        var usb = _linkManager.Usb;
        usb.OnStatusChanged += msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UsbStatusText.Text = msg);
        usb.OnActiveChanged += active =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                UsbButton.Content = active ? "停止 USB" : "USB 直连");

        // ── 启动 LAN 常驻服务 ──
        _ = _linkManager.StartLanAsync();

        // ── P2P 冷启动自动常驻（与 LAN 并行，直到软件关闭） ──
        _ = _linkManager.StartP2pAsync();

        // ── 蓝牙常驻监听（与 LAN 并行，等待手机连接） ──
        _ = _linkManager.StartBluetoothAsync();

        // ── USB 常驻监听（与 LAN 并行，等待手机连接） ──
        _ = _linkManager.StartUsbAsync();
    }

    // ── P2P 按钮（P2P 已常驻，按钮仅用于重新生成 QR 码） ──
    private async void OnP2pClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // P2P 常驻不可停止，点击仅刷新 QR 码
        await _linkManager.StopP2pAsync();
        await _linkManager.StartP2pAsync();
    }

    // ── 蓝牙按钮（启动/停止） ──
    private async void OnBtClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_linkManager.IsBluetoothActive)
            await _linkManager.StopBluetoothAsync();
        else
            await _linkManager.StartBluetoothAsync();
    }

    // ── USB 按钮（启动/停止） ──
    private async void OnUsbClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_linkManager.IsUsbActive)
            await _linkManager.StopUsbAsync();
        else
            await _linkManager.StartUsbAsync();
    }

    /// <summary>从托盘恢复窗口</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
