using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// UdpTransport — ITransport 的 UDP 实现
///
/// 包裹 UdpClient，支持 server 模式（仅绑定端口）和 client 模式（指定远程主机）。
/// </summary>
public sealed class UdpTransport : ITransport, IDisposable
{
    private readonly UdpClient _client;
    private readonly string? _remoteHost;
    private readonly int _remotePort;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public UdpTransport(int localPort, string? remoteHost = null, int? remotePort = null)
    {
        _client = new UdpClient(localPort);
        _remoteHost = remoteHost;
        _remotePort = remotePort ?? localPort;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_cts != null) return;

        if (_remoteHost != null)
            _client.Connect(_remoteHost, _remotePort);

        _cts = new CancellationTokenSource();
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try
        {
            if (_remoteHost != null)
            {
                // client 模式：Connect 后直接 Send
                await _client.SendAsync(data.ToArray(), data.Length);
            }
            else
            {
                // server 模式需要知道目标地址，通过 PacketReceived 事件中记录
                // 这里暂不支持无 Connect 的发送，业务层需自行处理
                Log.W("UdpTransport", "SendAsync called in server mode without remote address");
            }
        }
        catch (Exception ex)
        {
            Log.E("UdpTransport", $"SendAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// 向指定端点发送数据（server 模式下使用）
    /// </summary>
    public async Task SendToAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct = default)
    {
        await _client.SendAsync(data, data.Length, remoteEp);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client.ReceiveAsync(ct);
                PacketReceived?.Invoke(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Log.E("UdpTransport", $"ReceiveLoop error: {ex.Message}");
            }
        }
    }

    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public bool IsConnected => _cts != null && !_cts.IsCancellationRequested;
    public TransportType Type => TransportType.Udp;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _client.Dispose();
    }
}
