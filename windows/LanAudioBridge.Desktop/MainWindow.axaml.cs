using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Factory;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 主窗口 — 打开即自动接收，关闭即最小化到托盘
///
/// 虚拟麦克风模式（管线 3/4）直接写音频到 CABLE Input 端点，
/// 用户需在目标应用中手动选择 CABLE Output 作为麦克风
///
/// 系统托盘功能：关闭窗口时最小化到托盘，右键可退出
/// </summary>
public partial class MainWindow : Window
{
    // ── 核心模块 ──
    private readonly AudioEngine _engine;                               // 音频引擎（UDP接收+解码+路由）
    private readonly MdnsPublisher _mdns = MdnsPublisher.Create(Environment.MachineName, 12345);  // mDNS 服务发布
    private readonly HandshakeServer _hs;                                // 握手服务
    private readonly ConnectionStateManager _stateManager = new();       // 连接状态管理器

    public MainWindow()
    {
        InitializeComponent();

        // 通过 PlatformFactory 创建传输层实例
        var audioTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, 12345);
        var hsTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, 12347);

        _engine = new AudioEngine(audioTransport);
        _hs = new HandshakeServer(hsTransport, OnHandshakeRoute);
        Loaded += OnLoaded;

        // ── 关闭时最小化到托盘，不退出 ──
        // Avalonia 原生 TrayIcon 需要第三方插件；
        // 当前做法：Closing 事件取消关闭，隐藏窗口
        Closing += (_, e) =>
        {
            // 改为隐藏而非退出
            e.Cancel = true;
            Hide();
        };

        // ── 窗口关闭时触发状态保留 ──
        Closed += (_, _) =>
        {
            // 不停止服务，保持后台运行
        };
    }

    // ── 握手路由回调（收到 HELLO/ROUTE 时触发） ──
    private bool OnHandshakeRoute(int route)
    {
        route = Math.Clamp(route, 0, 3);

        var mode = route switch
        {
            0 => AudioRouter.RouteMode.SpeakerOnly,
            1 => AudioRouter.RouteMode.SpeakerOnly,
            2 => AudioRouter.RouteMode.MicOnly,
            3 => AudioRouter.RouteMode.MicOnlySys,
            _ => AudioRouter.RouteMode.SpeakerOnly,
        };

        if (!_engine.Router.SetMode(mode))
        {
            Log.W("MainWindow", $"Route {route} rejected (CABLE not available?)");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _stateManager.Update(ConnectionState.Error);
                StatusText.Text = $"路线 {route + 1} 切换失败";
            });
            return false;
        }

        // 推流中切路线不降级状态（保持 STREAMING）
        if (_stateManager.State != ConnectionState.Streaming)
        {
            _stateManager.Update(ConnectionState.Connected);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = $"当前路线 {route + 1}：{ModeLabel(route)}";
        });
        return true;
    }

    private static string ModeLabel(int route) => route switch
    {
        0 => "手机系统音频 → 电脑扬声器",
        1 => "手机系统音频 + 麦克风 → 电脑扬声器",
        2 => "手机麦克风 → 电脑虚拟麦克风",
        3 => "手机系统音频 → 电脑虚拟麦克风",
        _ => "未知路线",
    };

    // ── 窗口加载完成 ──
    private void OnLoaded(object? sender, EventArgs e)
    {
        StatusText.Text = "就绪：等待手机连接";
        _stateManager.ClearLastReason();
        UpdateConnectionState(ConnectionState.Disconnected);

        // ── 连接状态 → UI 更新 ──
        _stateManager.OnStateChanged += state =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateConnectionState(state));
        };

        // HELLO 收到 → 连接中
        _hs.OnHelloReceived += _ => _stateManager.Update(ConnectionState.Connecting);

        // 第一帧 Opus 解码 → 音频传输中
        _engine.OnFirstFrameDecoded += () => _stateManager.Update(ConnectionState.Streaming);

        // 音频超时（连续 3 秒未收到） → 连接恢复中
        // 不发起重连，等 Android 端发 HELLO 自动恢复
        _engine.OnAudioTimeout += () =>
        {
            if (_stateManager.State == ConnectionState.Streaming)
            {
                _stateManager.Update(ConnectionState.Reconnecting);
            }
        };

        // ── 音量滑块 ──
        VolumeText.Text = $"{(int)(VolumeSlider.Value * 100)}%";
        VolumeSlider.ValueChanged += (_, _) =>
        {
            float vol = (float)VolumeSlider.Value;
            _engine.Volume = vol;
            VolumeText.Text = $"{(int)(vol * 100)}%";
        };

        // ── 路由状态通知 ──
        _engine.Router.OnMicOutputChanged += toMic =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = toMic
                    ? "虚拟麦克风模式：音频已写入 CABLE Input，请在目标软件中选择 CABLE Output"
                    : "扬声器模式：音频播放到系统默认扬声器";
            });
        };
        _engine.Router.OnError += msg =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = msg);
        };
        _hs.OnError += msg =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = msg);
        };

        // ── 启动所有服务 ──
        _mdns.Start();
        _hs.Start();
        _engine.Start();
    }

    // ── 更新连接状态 UI ──
    private void UpdateConnectionState(ConnectionState state)
    {
        ConnectionStateText.Text = state.ToChineseLabel();
    }

    /// <summary>从托盘恢复窗口</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
