using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Adapters;
using LanAudioBridge.Desktop.Links;
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
    private WifiDirectP2pHelper? _p2pHelper;                              // WiFi Direct P2P 建链辅助器

    public MainWindow()
    {
        InitializeComponent();

        // 通过 PlatformFactory 创建传输层和渲染器实例
        var audioTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, 12345);
        var hsTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, 12347);
        var speakerRenderer = PlatformFactory.CreateRenderer(useCable: false);
        var cableRenderer = PlatformFactory.CreateRenderer(useCable: true);

        _engine = new AudioEngine(audioTransport, speakerRenderer, cableRenderer);
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
            0 => AudioRouter.RouteMode.SpeakerOnly,   // 系统音频 → 扬声器
            1 => AudioRouter.RouteMode.Both,           // 系统音频 + 麦克风 → 扬声器（混音）
            2 => AudioRouter.RouteMode.MicOnly,        // 麦克风 → 虚拟麦克风
            3 => AudioRouter.RouteMode.MicOnlySys,     // 系统音频 → 虚拟麦克风
            _ => AudioRouter.RouteMode.SpeakerOnly,
        };

        // 注意：不能在此处调用 Router.Stop()！
        // Stop() 置空 _speakerOut 后，SetMode(相同模式) 走短路分支返回 false → HELLO_NACK → 握手失败
        // SetMode 内部已包含完整 stop-start 逻辑，无需外部预 Stop

        // 重置音频会话：Android 每次 HELLO 后会重启推流（seq 从 0 开始），
        // 必须清空 JitterBuffer 和 seq 追踪，否则新会话帧被当作“迟到包”丢弃
        _engine.ResetSession();

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

    // ── WiFi Direct P2P ──

    private async void OnP2pClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_p2pHelper != null)
        {
            // 已启动 → 停止
            await _p2pHelper.StopAsync();
            _p2pHelper.Dispose();
            _p2pHelper = null;
            _hs.ExpectedToken = null;
            P2pButton.Content = "启动 P2P";
            QrImage.IsVisible = false;
            P2pDeviceLabel.IsVisible = false;
            P2pProgressPanel.IsVisible = false;
            P2pStatusText.Text = "";
            P2pProgressRing.IsIndeterminate = true;
            return;
        }

        // 创建 P2P 建链辅助器（Windows 做客户端，连接 Android GO）
        _p2pHelper = new WifiDirectP2pHelper();
        _p2pHelper.OnStatusChanged += msg =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                P2pProgressPanel.IsVisible = true;
                P2pStatusText.Text = msg;
            });
        };

        // QR 码立即显示（含 device name + token，手机扫码后创建 P2P Group）
        var qrContent = QrCodeHelper.BuildQrPayload(_p2pHelper.DeviceName, _p2pHelper.Token);
        QrImage.Source = QrCodeHelper.Generate(qrContent);
        QrImage.IsVisible = true;
        P2pDeviceLabel.Text = $"设备：{_p2pHelper.DeviceName}  请手机扫码";
        P2pDeviceLabel.IsVisible = true;

        // P2P 连接成功后：主动发 HELLO 到 Android GO
        _p2pHelper.OnConnected += () =>
        {
            _ = Task.Run(() => SendHelloToAndroidGo());
        };

        // 设置 Token 校验
        _hs.ExpectedToken = _p2pHelper.Token;
        P2pButton.Content = "停止 P2P";

        // 启动发现循环（异步，不阻塞 UI）
        await _p2pHelper.StartAsync();
    }

    /// <summary>P2P 连接后主动向 Android GO 发 HELLO 握手</summary>
    private async Task SendHelloToAndroidGo()
    {
        ITransport? transport = null;
        try
        {
            var goIp = _p2pHelper?.GoIp ?? WifiDirectLink.GoIp;
            var token = _p2pHelper?.Token ?? "";
            Log.I("MainWindow", $"Sending HELLO to Android GO: {goIp}:{WifiDirectLink.HandshakePort}");

            // 通过 PlatformFactory 创建传输层（不直调 UdpClient）
            transport = PlatformFactory.CreateTransport(
                TransportType.Udp, goIp, WifiDirectLink.HandshakePort);
            var protocol = PlatformFactory.CreateProtocol();
            await transport.ConnectAsync();

            // 构建 HELLO 包（payload: 1B route + 8B token）
            var tokenBytes = Encoding.ASCII.GetBytes(token);
            var payload = new byte[9];
            payload[0] = 0; // route mode 0
            Array.Copy(tokenBytes, 0, payload, 1, Math.Min(8, tokenBytes.Length));

            var packet = new Packet
            {
                Type = PacketType.Hello,
                LinkType = WifiDirectLink.LinkTypeId,
                Sequence = 0,
                Payload = payload
            };
            var encoded = protocol.Encode(packet);

            // 重发 3 次（P2P 链路 ARP 可能丢弃前几包）
            for (int i = 0; i < 3; i++)
            {
                await transport.SendAsync(encoded);
                Log.I("MainWindow", $"HELLO sent to {goIp}:{WifiDirectLink.HandshakePort} (attempt {i + 1}/3)");

                // 等待 HELLO_ACK（2s 超时）
                try
                {
                    var reply = await WaitForPacketAsync(transport, 2000);
                    if (reply != null)
                    {
                        var decoded = protocol.Decode(reply.Value.Span);
                        if (decoded.HasValue && decoded.Value.Type == PacketType.HelloAck)
                        {
                            int route = 0;
                            if (decoded.Value.Payload.Length >= 1)
                                route = Math.Clamp((int)decoded.Value.Payload[0], 0, 3);

                            Log.I("MainWindow", $"HELLO_ACK received! P2P handshake OK, route={route}");
                            OnHandshakeRoute(route);

                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                P2pStatusText.Text = $"P2P 握手成功 ✓ local={_p2pHelper?.LocalIp} go={goIp} route={route}";
                                P2pProgressRing.IsIndeterminate = false;
                                P2pProgressRing.Value = 100;
                            });
                            _stateManager.Update(ConnectionState.Connected);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { /* timeout, retry */ }
            }

            Log.W("MainWindow", "P2P handshake failed: no HELLO_ACK after 3 attempts");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                P2pStatusText.Text = "P2P 握手失败（手机未响应）";
            });
        }
        catch (Exception ex)
        {
            Log.E("MainWindow", $"SendHelloToAndroidGo error: {ex.Message}");
        }
        finally
        {
            if (transport != null) await transport.DisconnectAsync();
        }
    }

    /// <summary>等待 ITransport 收到一个数据包（超时返回 null）</summary>
    private static async Task<ReadOnlyMemory<byte>?> WaitForPacketAsync(ITransport transport, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>();
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        transport.PacketReceived += data => tcs.TrySetResult(data);
        try { return await tcs.Task; }
        catch (OperationCanceledException) { return null; }
    }
}
