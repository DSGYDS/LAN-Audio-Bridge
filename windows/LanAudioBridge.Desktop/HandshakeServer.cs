using System;
using System.Threading;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 握手服务 — 监听 UDP 12347
/// - HELLO (type=0x01) → 回复 HELLO_ACK/HELLO_NACK，触发 OnHelloReceived + 路由切换
/// - ROUTE (type=0x04) → 热切路由（推流中切换路线，回复 ROUTE_ACK）
/// </summary>
public sealed class HandshakeServer : IDisposable
{
    private readonly ITransport? _transport;
    private readonly IPacketProtocol _protocol = new LanAudioBridge.Core.Adapters.PacketHeaderAdapter();
    private volatile bool _running;
    private Func<int, bool>? _onModeChange;
    public Action<string>? OnError;

    // ── ROUTE 防抖（快速切换只执行最后一次） ──
    private Timer? _routeDebounceTimer;
    private int _pendingRoute = -1;
    private readonly object _routeLock = new();
    private const int RouteDebounceMs = 150;

    /// <summary>收到 HELLO 握手请求时触发（仅 HELLO，不含 ROUTE 切换）</summary>
    public event Action<int>? OnHelloReceived;

    /// <param name="transport">ITransport 实例（由 PlatformFactory 创建，端口 12347）</param>
    /// <param name="onModeChange">收到 HELLO/ROUTE 时触发，参数为路由模式 0-3</param>
    public HandshakeServer(ITransport? transport = null, Func<int, bool>? onModeChange = null)
    {
        _transport = transport;
        _onModeChange = onModeChange;
    }

    // ── 生命周期 ──

    public void Start()
    {
        if (_running) return;

        if (_transport == null)
        {
            var msg = "握手服务未提供 ITransport，无法启动";
            Log.E("HandshakeServer", msg);
            OnError?.Invoke(msg);
            return;
        }

        _running = true;
        _transport.PacketReceived += OnPacketReceived;
        _ = _transport.ConnectAsync();
    }

    public void Stop()
    {
        _running = false;
        if (_transport != null)
        {
            _transport.PacketReceived -= OnPacketReceived;
            _ = _transport.DisconnectAsync();
        }
    }

    public void Dispose() { Stop(); }

    // ── ROUTE 防抖逻辑 ──
    // 快速连续切换时，只执行最后一次（避免音频设备反复 stop-start 导致失效）
    private void DebouncedRouteChange(int newMode)
    {
        lock (_routeLock)
        {
            _pendingRoute = newMode;
            _routeDebounceTimer?.Dispose();
            _routeDebounceTimer = new Timer(_ => ApplyPendingRoute(), null, RouteDebounceMs, Timeout.Infinite);
        }
    }

    private void ApplyPendingRoute()
    {
        int mode;
        lock (_routeLock)
        {
            mode = _pendingRoute;
            _pendingRoute = -1;
        }
        if (mode >= 0)
        {
            _onModeChange?.Invoke(mode);
        }
    }

    // ── 数据包接收回调 ──
    // 通过 IPacketProtocol 统一解码，不直接调用 PacketHeader
    private void OnPacketReceived(ReadOnlyMemory<byte> data)
    {
        if (!_running) return;

        try
        {
            // 通过 IPacketProtocol 解码
            var packet = _protocol.Decode(data.Span);
            if (packet == null) return; // 校验失败，丢弃

            var type = packet.Value.Type;
            var payload = packet.Value.Payload;

            // ── HELLO — 手机端发起首次连接（payload: 1B routeMode） ──
            if (type == PacketType.Hello)
            {
                int routeMode = 0;
                if (payload.Length >= 1)
                    routeMode = Math.Clamp((int)payload[0], 0, 3);

                OnHelloReceived?.Invoke(routeMode);
                bool accepted = _onModeChange?.Invoke(routeMode) ?? true;

                // 回复 HELLO_ACK 或 HELLO_NACK
                var replyType = accepted ? PacketType.HelloAck : PacketType.HelloNack;
                var replyPacket = new Packet { Type = replyType, Sequence = 0, Payload = Array.Empty<byte>() };
                _ = _transport!.SendAsync(_protocol.Encode(replyPacket));
            }
            // ── ROUTE — 推流中热切路线（payload: 1B newRouteMode） ──
            else if (type == PacketType.Route)
            {
                if (payload.Length >= 1)
                {
                    int newMode = Math.Clamp((int)payload[0], 0, 3);
                    DebouncedRouteChange(newMode);
                }

                // 回复 ROUTE_ACK 确认路由切换
                var ackPacket = new Packet { Type = PacketType.RouteAck, Sequence = 0, Payload = Array.Empty<byte>() };
                _ = _transport!.SendAsync(_protocol.Encode(ackPacket));
            }
        }
        catch (Exception ex)
        {
            Log.E("HandshakeServer", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
