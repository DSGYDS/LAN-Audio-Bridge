using System;
using System.Threading.Tasks;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Adapters;
using LanAudioBridge.Core.Factory;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// WifiLanLink — WiFi LAN 链路（完整实现，常驻服务）
///
/// 职责：mDNS 发布 + HandshakeServer + AudioEngine + 路由切换。
/// 与 WiFi Direct / 蓝牙 / USB 完全解耦。
///
/// 数据通路：UDP 12345 接收音频包 → AudioEngine（Opus 解码 → JitterBuffer → 播放）
/// 握手方向：Android 发 HELLO(route) → Windows 回 HELLO_ACK → 设置 AudioRouter 模式
/// 发现机制：mDNS 发布 _lan-audio._udp 服务，Android 端扫描发现
/// </summary>
public sealed class WifiLanLink : ILink
{
    private const string Tag = "WifiLanLink";

    // ── 链路常量 ──
    public const byte LinkTypeId = 0x01;
    public const int AudioPort = 12345;
    public const int HandshakePort = 12347;
    public const string MdnsServiceType = "_lan-audio._udp";

    // ── 核心模块 ──
    private bool _started;
    private readonly AudioEngine _engine;
    private readonly MdnsPublisher _mdns;
    private readonly HandshakeServer _hs;
    private readonly ConnectionStateManager _stateManager;

    // ── 事件（LinkManager / UI 订阅） ──
    public Action<string>? OnStatusChanged;
    public Action<ConnectionState>? OnStateChanged;

    // ── 公开属性 ──
    public bool IsActive => true; // LAN 常驻
    public AudioEngine Engine => _engine;
    public HandshakeServer HandshakeServer => _hs;
    public ConnectionStateManager StateManager => _stateManager;

    public float Volume
    {
        get => _engine.Volume;
        set => _engine.Volume = value;
    }

    /// <summary>构造函数：创建 UDP Transport(音频 12345 + 握手 12347) + AudioEngine + HandshakeServer + mDNS 发布器</summary>
    public WifiLanLink(ConnectionStateManager stateManager)
    {
        _stateManager = stateManager;

        var audioTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, AudioPort);
        var hsTransport = PlatformFactory.CreateTransport(TransportType.Udp, null, HandshakePort);
        var speakerRenderer = PlatformFactory.CreateRenderer(useCable: false);
        var cableRenderer = PlatformFactory.CreateRenderer(useCable: true);

        _engine = new AudioEngine(audioTransport, speakerRenderer, cableRenderer);
        _hs = new HandshakeServer(hsTransport, OnHandshakeRoute);
        _mdns = MdnsPublisher.Create(Environment.MachineName, AudioPort);
    }

    // ── ILink 实现 ──

    /// <summary>
    /// 启动 LAN 常驻服务（开机即启动，始终等待手机连接）
    /// 流程：订阅状态事件 → 启动 mDNS 发布 → 启动 HandshakeServer → 启动 AudioEngine
    /// </summary>
    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        _stateManager.ClearLastReason();

        _stateManager.OnStateChanged += state => OnStateChanged?.Invoke(state);
        _hs.OnHelloReceived += _ => _stateManager.Update(ConnectionState.Connecting);
        _engine.OnFirstFrameDecoded += () => _stateManager.Update(ConnectionState.Streaming);
        _engine.OnAudioTimeout += () =>
        {
            if (_stateManager.State == ConnectionState.Streaming)
                _stateManager.Update(ConnectionState.Reconnecting);
        };

        _engine.Router.OnMicOutputChanged += toMic =>
        {
            OnStatusChanged?.Invoke(toMic
                ? "虚拟麦克风模式：音频已写入 CABLE Input，请在目标软件中选择 CABLE Output"
                : "扬声器模式：音频播放到系统默认扬声器");
        };
        _engine.Router.OnError += msg => OnStatusChanged?.Invoke(msg);
        _hs.OnError += msg => OnStatusChanged?.Invoke(msg);

        _mdns.Start();
        _hs.Start();
        _engine.Start();

        OnStatusChanged?.Invoke("就绪：等待手机连接");
        OnStateChanged?.Invoke(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    /// <summary>停止 LAN 链路（仅停止 AudioEngine，mDNS 和 HandshakeServer 保持运行）</summary>
    public Task StopAsync()
    {
        _engine.Stop();
        return Task.CompletedTask;
    }

    /// <summary>处理路由切换（供 WifiDirectLink P2P 握手成功后调用）</summary>
    public bool HandleRoute(int route) => OnHandshakeRoute(route);

    // ── 握手路由回调（收到 HELLO 或 ROUTE 包时触发，设置 AudioRouter 模式） ──

    /// <summary>
    /// 处理握手路由：重置会话 + 设置 AudioRouter 模式 + 更新状态机。
    /// 路线映射：0/1=扬声器，2=麦克风→CABLE，3=系统音频→CABLE
    /// </summary>

    private bool OnHandshakeRoute(int route)
    {
        route = Math.Clamp(route, 0, 3);

        var mode = route switch
        {
            0 => AudioRouter.RouteMode.SpeakerOnly,   // 系统音频 → 扬声器
            1 => AudioRouter.RouteMode.SpeakerOnly,   // 系统音频+麦克风混音 → 扬声器（混音在 Android 端完成，Windows 端只播放）
            2 => AudioRouter.RouteMode.MicOnly,        // 麦克风 → 虚拟麦克风
            3 => AudioRouter.RouteMode.MicOnlySys,     // 系统音频 → 虚拟麦克风
            _ => AudioRouter.RouteMode.SpeakerOnly,
        };

        _engine.ResetSession();

        if (!_engine.Router.SetMode(mode))
        {
            Log.W(Tag, $"Route {route} rejected (CABLE not available?)");
            _stateManager.Update(ConnectionState.Error);
            OnStatusChanged?.Invoke($"路线 {route + 1} 切换失败");
            return false;
        }

        if (_stateManager.State != ConnectionState.Streaming)
            _stateManager.Update(ConnectionState.Connected);

        OnStatusChanged?.Invoke($"当前路线 {route + 1}：{ModeLabel(route)}");
        return true;
    }

    /// <summary>路线编号 → 中文描述（UI 显示用）</summary>
    public static string ModeLabel(int route) => route switch
    {
        0 => "手机系统音频 → 电脑扬声器",
        1 => "手机系统音频 + 麦克风 → 电脑扬声器",
        2 => "手机麦克风 → 电脑虚拟麦克风",
        3 => "手机系统音频 → 电脑虚拟麦克风",
        _ => "未知路线",
    };

    public void Dispose()
    {
        _engine.Dispose();
    }
}
