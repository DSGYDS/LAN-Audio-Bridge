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

    public Task StopAsync()
    {
        _engine.Stop();
        return Task.CompletedTask;
    }

    /// <summary>处理路由切换（供 WifiDirectLink P2P 握手成功后调用）</summary>
    public bool HandleRoute(int route) => OnHandshakeRoute(route);

    // ── 握手路由回调 ──

    private bool OnHandshakeRoute(int route)
    {
        route = Math.Clamp(route, 0, 3);

        var mode = route switch
        {
            0 => AudioRouter.RouteMode.SpeakerOnly,
            1 => AudioRouter.RouteMode.Both,
            2 => AudioRouter.RouteMode.MicOnly,
            3 => AudioRouter.RouteMode.MicOnlySys,
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
