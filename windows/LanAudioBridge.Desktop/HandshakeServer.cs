using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 握手服务 — 监听 UDP 12347
/// - HELLO (type=0x01) → 回复 HELLO_ACK/HELLO_NACK，触发 OnHelloReceived + 路由切换
/// - ROUTE (type=0x04) → 热切路由（推流中切换路线，不回复 ACK）
/// </summary>
public sealed class HandshakeServer : IDisposable
{
    private const int Port = 12347;
    private const int RecvTimeoutMs = 1000;

    private UdpClient? _sock;
    private Thread? _thread;
    private volatile bool _running;
    private Func<int, bool>? _onModeChange;
    public Action<string>? OnError;

    /// <summary>收到 HELLO 握手请求时触发（仅 HELLO，不含 ROUTE 切换）</summary>
    public event Action<int>? OnHelloReceived;

    /// <param name="onModeChange">收到 HELLO/ROUTE 时触发，参数为路由模式 0-3</param>
    public HandshakeServer(Func<int, bool>? onModeChange = null)
    {
        _onModeChange = onModeChange;
    }

    /// <summary>运行中也可设置路由回调</summary>
    public void SetModeCallback(Func<int, bool> cb) => _onModeChange = cb;

    // ── 生命周期 ──

    public void Start()
    {
        if (_running) return;
        try
        {
            _sock = new UdpClient(Port);
            _sock.Client.ReceiveTimeout = RecvTimeoutMs;
        }
        catch (Exception ex)
        {
            _sock?.Dispose();
            _sock = null;
            var msg = $"握手端口 {Port} 启动失败: {ex.Message}";
            Log.E("HandshakeServer", msg);
            OnError?.Invoke(msg);
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "handshake" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _sock?.Close();
        _sock?.Dispose();
        _sock = null;
        _thread?.Join(RecvTimeoutMs + 500);
    }

    public void Dispose() { Stop(); _sock?.Dispose(); }

    // ── 接收循环 ──
    // 所有包使用统一的 PacketHeader 14B 二进制格式
    private void Loop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                var sock = _sock;
                if (sock == null) { Thread.Sleep(10); continue; }

                byte[] data;
                try
                {
                    data = sock.Receive(ref ep);
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }

                // 解析 PacketHeader（严格校验 Magic/Version/PayloadLength）
                var info = PacketHeader.TryDecode(data);
                if (info == null) continue; // 校验失败，丢弃

                var type = info.Value.Type;
                var seq = info.Value.Seq;
                var payloadLen = info.Value.PayloadLen;

                // ── HELLO — 手机端发起首次连接（payload: 1B routeMode） ──
                if (type == (byte)PacketType.Hello)
                {
                    int routeMode = 0;
                    if (payloadLen >= 1)
                        routeMode = Math.Clamp((int)data[PacketHeader.HeaderSize], 0, 3);

                    OnHelloReceived?.Invoke(routeMode);
                    bool accepted = _onModeChange?.Invoke(routeMode) ?? true;

                    // 回复 HELLO_ACK 或 HELLO_NACK
                    var replyType = accepted ? PacketType.HelloAck : PacketType.HelloNack;
                    var reply = PacketHeader.EncodeHeader((byte)replyType, 0, 0);
                    sock.Send(reply, reply.Length, ep);
                }
                // ── ROUTE — 推流中热切路线（payload: 1B newRouteMode） ──
                else if (type == (byte)PacketType.Route)
                {
                    int newMode = 0;
                    if (payloadLen >= 1)
                    {
                        newMode = Math.Clamp((int)data[PacketHeader.HeaderSize], 0, 3);
                        _onModeChange?.Invoke(newMode);
                    }

                    // 回复 ROUTE_ACK 确认路由切换
                    var ack = PacketHeader.EncodeHeader((byte)PacketType.RouteAck, 0, 0);
                    sock.Send(ack, ack.Length, ep);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Log.E("HandshakeServer", $"{ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(10);
            }
        }
    }
}
