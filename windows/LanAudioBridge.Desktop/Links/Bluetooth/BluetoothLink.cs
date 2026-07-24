using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Adapters;
using LanAudioBridge.Core.Factory;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// BluetoothLink — 蓝牙 RFCOMM 链路（常驻服务）
///
/// 职责：常驻监听 RFCOMM → 等待 Android 连接 → 被动接收 HELLO 回 ACK → AudioEngine 播放。
/// 与 WifiLanLink 对称：开机即启动，始终等待手机连接。
///
/// 数据通路：BluetoothTransport.PacketReceived → AudioEngine（Opus 解码 → 播放）
/// 握手方向：Android 发 HELLO(token) → Windows 校验 → 回 HELLO_ACK(route)（与 LAN 一致）
/// </summary>
public sealed class BluetoothLink : ILink
{
    private const string Tag = "BluetoothLink";

    // ── 链路常量 ──
    public const byte LinkTypeId = 0x03;
    private const string BtToken = "LABRIDGE";  // 必须 ≤ 8 字符（payload 限制）
    private const int HelloTimeoutMs = 60_000;  // 等待 HELLO 超时

    // ── 核心模块 ──
    private BluetoothTransport? _transport;
    private AudioEngine? _engine;
    private readonly ConnectionStateManager _stateManager;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private volatile bool _started;

    // ── 事件（LinkManager / UI 订阅） ──
    public Action<string>? OnStatusChanged;
    public Action<bool>? OnActiveChanged;
    /// <summary>蓝牙会话开始（手机连接+握手成功），LinkManager 用于暂停 LAN 引擎</summary>
    public Action? OnSessionStarted;
    /// <summary>蓝牙会话结束（手机断开），LinkManager 用于恢复 LAN 引擎</summary>
    public Action? OnSessionEnded;

    public bool IsActive => _started;

    public BluetoothLink(ConnectionStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    // ── ILink 实现 ──

    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 启动常驻监听循环（后台）
        _listenLoop = Task.Run(() => ListenLoopAsync(ct));

        OnStatusChanged?.Invoke("蓝牙：就绪，等待手机连接");
        OnActiveChanged?.Invoke(true);
        Log.I(Tag, "Bluetooth link started (resident)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_listenLoop != null)
        {
            try { await _listenLoop; } catch { }
            _listenLoop = null;
        }

        await CleanupSessionAsync();
        _transport?.StopListening();
        _transport?.Dispose();
        _transport = null;

        OnStatusChanged?.Invoke("蓝牙：已停止");
        OnActiveChanged?.Invoke(false);
    }

    // ── 常驻监听循环 ──

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        _transport = new BluetoothTransport();
        _transport.StartListening();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 等待 Android 连接
                OnStatusChanged?.Invoke("蓝牙：等待手机连接...");
                var connected = await _transport.WaitForConnectionAsync(ct);
                if (!connected) continue;

                // 连接建立 → 等待 HELLO
                OnStatusChanged?.Invoke("蓝牙：手机已连接，等待握手...");
                var route = await WaitForHelloAsync(ct);
                if (route < 0)
                {
                    OnStatusChanged?.Invoke("蓝牙：握手失败，重新等待...");
                    await _transport.DisconnectAsync();
                    continue;
                }

                // 握手成功 → 创建 AudioEngine → 播放
                OnSessionStarted?.Invoke();
                StartAudioEngine(route);
                OnStatusChanged?.Invoke($"蓝牙：推流中 ✓ 路线{route + 1}");

                // 监听 ROUTE 热切包（AudioEngine 只处理 Audio 包，ROUTE 由此处理）
                _transport.PacketReceived += OnRoutePacket;

                // 等待连接断开（ReadLoop 退出 = 连接断开）
                await WaitForDisconnectAsync(ct);

                // 断开 → 清理 → 重新等待
                _transport.PacketReceived -= OnRoutePacket;
                OnSessionEnded?.Invoke();
                OnStatusChanged?.Invoke("蓝牙：手机断开，重新等待...");
                CleanupAudioEngine();
                await _transport.DisconnectAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.E(Tag, $"ListenLoop error: {ex.Message}");
                if (!ct.IsCancellationRequested)
                    await Task.Delay(2000, ct);  // 防止快速循环
            }
        }
    }

    // ── 被动握手：等待 Android HELLO → 校验 → 回 ACK ──

    private async Task<int> WaitForHelloAsync(CancellationToken ct)
    {
        if (_transport == null) return -1;

        var protocol = PlatformFactory.CreateProtocol();
        var tcs = new TaskCompletionSource<int>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HelloTimeoutMs);
        using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(-1));

        Action<ReadOnlyMemory<byte>> handler = data =>
        {
            var decoded = protocol.Decode(data.Span);
            if (!decoded.HasValue || decoded.Value.Type != PacketType.Hello) return;

            // 校验 token
            var payload = decoded.Value.Payload;
            var token = payload.Length >= 9
                ? Encoding.ASCII.GetString(payload[1..9]).TrimEnd('\0')
                : "";

            if (token != BtToken)
            {
                Log.W(Tag, $"Token mismatch: '{token}'");
                // 回 NACK
                var nack = new Packet { Type = PacketType.HelloNack, LinkType = LinkTypeId, Sequence = 0, Payload = Array.Empty<byte>() };
                _ = _transport.SendAsync(protocol.Encode(nack));
                tcs.TrySetResult(-1);
                return;
            }

            // 校验通过 → 回 ACK(route)
            int route = payload.Length >= 1 ? Math.Clamp(payload[0], (byte)0, (byte)3) : 0;
            var ack = new Packet { Type = PacketType.HelloAck, LinkType = LinkTypeId, Sequence = 0, Payload = new[] { (byte)route } };
            _ = _transport.SendAsync(protocol.Encode(ack));
            Log.I(Tag, $"HELLO verified, ACK sent (route={route})");
            tcs.TrySetResult(route);
        };

        _transport.PacketReceived += handler;
        try { return await tcs.Task; }
        finally { _transport.PacketReceived -= handler; }
    }

    // ── ROUTE 热切处理 ──

    private void OnRoutePacket(ReadOnlyMemory<byte> data)
    {
        var protocol = PlatformFactory.CreateProtocol();
        var decoded = protocol.Decode(data.Span);
        if (!decoded.HasValue || decoded.Value.Type != PacketType.Route) return;

        int route = decoded.Value.Payload.Length >= 1 ? Math.Clamp((int)decoded.Value.Payload[0], 0, 3) : 0;
        var mode = route switch
        {
            0 => AudioRouter.RouteMode.SpeakerOnly,
            1 => AudioRouter.RouteMode.SpeakerOnly,
            2 => AudioRouter.RouteMode.MicOnly,
            3 => AudioRouter.RouteMode.MicOnlySys,
            _ => AudioRouter.RouteMode.SpeakerOnly,
        };

        _engine?.Router.SetMode(mode);
        Log.I(Tag, $"Route hot-switch: route={route}, mode={mode}");
        OnStatusChanged?.Invoke($"蓝牙：路线{route + 1}");
    }

    // ── AudioEngine 管理 ──

    private void StartAudioEngine(int route)
    {
        var speaker = PlatformFactory.CreateRenderer(useCable: false);
        var cable = PlatformFactory.CreateRenderer(useCable: true);
        _engine = new AudioEngine(_transport, speaker, cable);

        var mode = route switch
        {
            0 => AudioRouter.RouteMode.SpeakerOnly,
            1 => AudioRouter.RouteMode.SpeakerOnly,
            2 => AudioRouter.RouteMode.MicOnly,
            3 => AudioRouter.RouteMode.MicOnlySys,
            _ => AudioRouter.RouteMode.SpeakerOnly,
        };
        _engine.Router.SetMode(mode);

        _engine.OnFirstFrameDecoded += () => _stateManager.Update(ConnectionState.Streaming);
        _engine.Start();
        _stateManager.Update(ConnectionState.Connected);
    }

    private void CleanupAudioEngine()
    {
        _engine?.Stop();
        _engine?.Dispose();
        _engine = null;
    }

    private async Task WaitForDisconnectAsync(CancellationToken ct)
    {
        // 等待 transport 连接断开（IsConnected 变 false）
        while (!ct.IsCancellationRequested && _transport?.IsConnected == true)
            await Task.Delay(500, ct);
    }

    private async Task CleanupSessionAsync()
    {
        CleanupAudioEngine();
        if (_transport != null)
            await _transport.DisconnectAsync();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _engine?.Dispose();
        _transport?.Dispose();
    }
}
