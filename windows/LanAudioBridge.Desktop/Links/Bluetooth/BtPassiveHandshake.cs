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
/// BtPassiveHandshake — 蓝牙链路被动握手 + ROUTE 热切处理
///
/// 职责：
///   1. 等待 Android HELLO → 校验 token → 回 HELLO_ACK(route)
///   2. 处理推流中 ROUTE 包 → 切换 AudioRouter 模式
///
/// 与 WifiDirect 的 P2pHandshakeServer 对称。
/// </summary>
internal static class BtPassiveHandshake
{
    private const string Tag = "BtHandshake";
    private const string BtToken = "LABRIDGE";  // 必须 ≤ 8 字符（payload 限制）
    private const int HelloTimeoutMs = 60_000;

    /// <summary>
    /// 等待 Android HELLO 并完成被动握手。
    /// 流程：注册 PacketReceived 回调 → 等待 HELLO 包 → 校验 token → 回 ACK/NACK。
    /// 返回 route（0-3），失败返回 -1。
    /// </summary>
    public static async Task<int> WaitForHelloAsync(BluetoothTransport transport, CancellationToken ct)
    {
        var protocol = PlatformFactory.CreateProtocol();
        var tcs = new TaskCompletionSource<int>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HelloTimeoutMs);
        using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(-1));

        Action<ReadOnlyMemory<byte>> handler = data =>
        {
            var decoded = protocol.Decode(data.Span);
            if (!decoded.HasValue || decoded.Value.Type != PacketType.Hello) return;

            var payload = decoded.Value.Payload;
            var token = payload.Length >= 9
                ? Encoding.ASCII.GetString(payload[1..9]).TrimEnd('\0')
                : "";

            if (token != BtToken)
            {
                Log.W(Tag, $"Token mismatch: '{token}'");
                var nack = new Packet { Type = PacketType.HelloNack, LinkType = BluetoothLink.LinkTypeId, Sequence = 0, Payload = Array.Empty<byte>() };
                _ = transport.SendAsync(protocol.Encode(nack));
                tcs.TrySetResult(-1);
                return;
            }

            int route = payload.Length >= 1 ? Math.Clamp(payload[0], (byte)0, (byte)3) : 0;
            var ack = new Packet { Type = PacketType.HelloAck, LinkType = BluetoothLink.LinkTypeId, Sequence = 0, Payload = new[] { (byte)route } };
            _ = transport.SendAsync(protocol.Encode(ack));
            Log.I(Tag, $"HELLO verified, ACK sent (route={route})");
            tcs.TrySetResult(route);
        };

        transport.PacketReceived += handler;
        try { return await tcs.Task; }
        finally { transport.PacketReceived -= handler; }
    }

    /// <summary>
    /// 处理 ROUTE 热切包。非 ROUTE 包直接忽略。
    /// 解码 payload[0] 为路线编号，映射到 AudioRouter 模式并切换。
    /// </summary>
    public static void HandleRoutePacket(ReadOnlyMemory<byte> data, AudioEngine? engine, Action<string>? onStatus)
    {
        var protocol = PlatformFactory.CreateProtocol();
        var decoded = protocol.Decode(data.Span);
        if (!decoded.HasValue || decoded.Value.Type != PacketType.Route) return;

        int route = decoded.Value.Payload.Length >= 1 ? Math.Clamp((int)decoded.Value.Payload[0], 0, 3) : 0;
        var mode = RouteToMode(route);

        engine?.Router.SetMode(mode);
        Log.I(Tag, $"Route hot-switch: route={route}, mode={mode}");
        onStatus?.Invoke($"蓝牙：路线{route + 1}");
    }

    /// <summary>路线编号 → AudioRouter 模式（与 WifiLanLink.OnHandshakeRoute 映射一致）</summary>
    public static AudioRouter.RouteMode RouteToMode(int route) => route switch
    {
        0 => AudioRouter.RouteMode.SpeakerOnly,
        1 => AudioRouter.RouteMode.SpeakerOnly,
        2 => AudioRouter.RouteMode.MicOnly,
        3 => AudioRouter.RouteMode.MicOnlySys,
        _ => AudioRouter.RouteMode.SpeakerOnly,
    };
}
