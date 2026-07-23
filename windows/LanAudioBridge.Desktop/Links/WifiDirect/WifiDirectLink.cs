using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Factory;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// WifiDirectLink — WiFi Direct P2P 链路（完整实现）
///
/// 职责：P2P 发现/连接 + QR 码 + 主动向 Android GO 发 HELLO 握手。
/// 与 LAN / 蓝牙 / USB 完全解耦。
/// </summary>
public sealed class WifiDirectLink : ILink
{
    private const string Tag = "WifiDirectLink";

    // ── 链路常量 ──
    public const byte LinkTypeId = 0x02;
    public const int AudioPort = 12345;
    public const int HandshakePort = 12347;
    public const string GoIp = "192.168.49.1";
    public const int DiscoverTimeoutMs = 120_000;
    public const int IpPollIntervalMs = 500;
    public const int IpPollMaxRetries = 30;

    // ── 核心模块 ──
    private WifiDirectP2pHelper? _p2pHelper;
    private readonly ConnectionStateManager _stateManager;
    private readonly Func<int, bool> _onHandshakeRoute;
    private CancellationTokenSource? _helloCts;  // 握手任务取消令牌

    // ── 事件（LinkManager / UI 订阅） ──
    public Action<string>? OnP2pStatusChanged;
    public Action<string?, string?>? OnQrChanged;
    public Action<bool>? OnP2pProgressVisible;
    public Action<bool, double>? OnP2pProgress;
    public Action<bool>? OnP2pActiveChanged;

    public bool IsActive => _p2pHelper != null;

    public WifiDirectLink(
        ConnectionStateManager stateManager,
        Func<int, bool> onHandshakeRoute)
    {
        _stateManager = stateManager;
        _onHandshakeRoute = onHandshakeRoute;
    }

    // ── ILink 实现 ──

    public async Task StartAsync()
    {
        if (_p2pHelper != null) return;

        _p2pHelper = new WifiDirectP2pHelper();
        _p2pHelper.OnStatusChanged += msg =>
        {
            OnP2pProgressVisible?.Invoke(true);
            OnP2pStatusChanged?.Invoke(msg);
        };

        var qrContent = QrCodeHelper.BuildQrPayload(_p2pHelper.DeviceName, _p2pHelper.Token);
        OnQrChanged?.Invoke(qrContent, _p2pHelper.DeviceName);

        _helloCts = new CancellationTokenSource();
        var helloToken = _helloCts.Token;
        _p2pHelper.OnConnected += () => _ = Task.Run(() => SendHelloToAndroidGo(helloToken));

        OnP2pActiveChanged?.Invoke(true);

        await _p2pHelper.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_p2pHelper == null) return;

        // 先取消正在进行的握手任务
        _helloCts?.Cancel();
        _helloCts?.Dispose();
        _helloCts = null;

        await _p2pHelper.StopAsync();
        _p2pHelper.Dispose();
        _p2pHelper = null;

        OnP2pActiveChanged?.Invoke(false);
        OnQrChanged?.Invoke(null, null);
        OnP2pProgressVisible?.Invoke(false);
        OnP2pStatusChanged?.Invoke("");
        OnP2pProgress?.Invoke(true, 0);
    }

    // ── P2P 握手（主动向 Android GO 发 HELLO） ──

    private const int HelloInitialDelayMs = 3_000;   // 等 Android 端 waitForHello 就绪
    private const int HelloMaxAttempts = 6;           // 重试次数（覆盖 Android 60s 监听窗口）
    private const int HelloTimeoutMs = 3_000;         // 每次等待 ACK 超时
    private const int HelloRetryDelayMs = 2_000;      // 重试间隔

    private async Task SendHelloToAndroidGo(CancellationToken ct)
    {
        ITransport? transport = null;
        try
        {
            var goIp = _p2pHelper?.GoIp ?? GoIp;
            var token = _p2pHelper?.Token ?? "";

            // 等待 P2P 网络稳定 + Android 端 waitForHello 开始监听
            Log.I(Tag, $"P2P connected, waiting {HelloInitialDelayMs}ms before HELLO...");
            OnP2pStatusChanged?.Invoke("P2P 已连接，等待手机端就绪...");
            await Task.Delay(HelloInitialDelayMs, ct);

            Log.I(Tag, $"Sending HELLO to Android GO: {goIp}:{HandshakePort}");
            transport = PlatformFactory.CreateTransport(TransportType.Udp, goIp, HandshakePort);
            var protocol = PlatformFactory.CreateProtocol();
            await transport.ConnectAsync();

            var tokenBytes = Encoding.ASCII.GetBytes(token);
            var payload = new byte[9];
            payload[0] = 0;
            Array.Copy(tokenBytes, 0, payload, 1, Math.Min(8, tokenBytes.Length));

            var packet = new Packet
            {
                Type = PacketType.Hello,
                LinkType = LinkTypeId,
                Sequence = 0,
                Payload = payload
            };
            var encoded = protocol.Encode(packet);

            for (int i = 0; i < HelloMaxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                await transport.SendAsync(encoded);
                Log.I(Tag, $"HELLO sent to {goIp}:{HandshakePort} (attempt {i + 1}/{HelloMaxAttempts})");
                OnP2pStatusChanged?.Invoke($"正在握手...（{i + 1}/{HelloMaxAttempts}）");

                try
                {
                    var reply = await WaitForPacketAsync(transport, HelloTimeoutMs);
                    if (reply != null)
                    {
                        var decoded = protocol.Decode(reply.Value.Span);
                        if (decoded.HasValue && decoded.Value.Type == PacketType.HelloAck)
                        {
                            int route = 0;
                            if (decoded.Value.Payload.Length >= 1)
                                route = Math.Clamp((int)decoded.Value.Payload[0], 0, 3);

                            Log.I(Tag, $"HELLO_ACK received! P2P handshake OK, route={route}");
                            _onHandshakeRoute(route);

                            // 配对持久化：握手成功即写入，后续冷启动免扫码
                            var paired = PairedDeviceStore.GetOrCreate();
                            paired.LastConnected = DateTime.Now;
                            PairedDeviceStore.Save(paired);

                            OnP2pStatusChanged?.Invoke($"P2P 握手成功 ✓ local={_p2pHelper?.LocalIp} go={goIp} route={route}");
                            OnP2pProgress?.Invoke(false, 100);
                            _stateManager.Update(ConnectionState.Connected);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout, retry */ }

                // 重试前等待
                if (i < HelloMaxAttempts - 1)
                    await Task.Delay(HelloRetryDelayMs, ct);
            }

            Log.W(Tag, $"P2P handshake failed: no HELLO_ACK after {HelloMaxAttempts} attempts");
            OnP2pStatusChanged?.Invoke("P2P 握手失败（手机未响应），等待重连...");
        }
        catch (OperationCanceledException)
        {
            Log.I(Tag, "HELLO task cancelled (P2P stopped)");
        }
        catch (Exception ex)
        {
            Log.E(Tag, $"SendHelloToAndroidGo error: {ex.Message}");
        }
        finally
        {
            if (transport != null) await transport.DisconnectAsync();
        }
    }

    private static async Task<ReadOnlyMemory<byte>?> WaitForPacketAsync(ITransport transport, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>();
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        Action<ReadOnlyMemory<byte>> handler = data => tcs.TrySetResult(data);
        transport.PacketReceived += handler;
        try { return await tcs.Task; }
        catch (OperationCanceledException) { return null; }
        finally { transport.PacketReceived -= handler; }
    }

    public void Dispose()
    {
        _p2pHelper?.Dispose();
    }
}
