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
    private IPEndPoint? _lastRemoteEp;  // server 模式：记录最后收到包的远端地址

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
                // server 模式：回复给最后发包的远端
                var ep = _lastRemoteEp;
                if (ep != null)
                    await _client.SendAsync(data.ToArray(), data.Length, ep);
                else
                    Log.W("UdpTransport", "SendAsync called in server mode but no remote endpoint known yet");
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
                _lastRemoteEp = result.RemoteEndPoint;  // 记录发送方地址（server 模式回复用）
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
