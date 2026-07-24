using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// BluetoothTransport — ITransport 的 RFCOMM 实现（Windows 端，Server 角色）
///
/// 职责：使用 32feet.NET BluetoothListener 常驻监听 Android 连接，
/// 在字节流上实现 PacketHeader 帧分割（15B header + payload）。
///
/// 生命周期：
///   StartListening() → 常驻监听（后台线程）
///   WaitForConnectionAsync() → 等待手机连接
///   连接建立后自动开始帧分割读取循环
/// </summary>
public sealed class BluetoothTransport : ITransport, IDisposable
{
    private const string Tag = "BluetoothTransport";

    /// <summary>自定义服务 UUID（与 Android 端一致）</summary>
    public static readonly Guid ServiceUuid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    private BluetoothListener? _listener;
    private BluetoothClient? _client;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private volatile bool _connected;

    // ── ITransport 实现 ──

    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public bool IsConnected => _connected;
    public TransportType Type => TransportType.Bluetooth;

    /// <summary>
    /// 启动 RFCOMM 监听（常驻，不阻塞）。
    /// 使用 32feet.NET BluetoothListener 注册 SDP 服务记录，
    /// Android 端可通过 UUID 发现并连接此服务。
    /// </summary>
    public void StartListening()
    {
        if (_listener != null) return;

        _listener = new BluetoothListener(ServiceUuid);
        _listener.Start();

        _cts = new CancellationTokenSource();
        Log.I(Tag, $"RFCOMM server listening (uuid={ServiceUuid})");
    }

    /// <summary>
    /// 等待 Android 连接（阻塞直到有连接或取消）。
    /// 连接建立后自动启动帧分割读取循环（ReadLoopAsync）。
    /// 每次新连接都会重置流状态，支持断开后重新等待。
    /// </summary>
    public async Task<bool> WaitForConnectionAsync(CancellationToken ct)
    {
        if (_listener == null) return false;

        try
        {
            Log.I(Tag, "Waiting for Android RFCOMM connection...");
            // 32feet.NET 的 AcceptBluetoothClientAsync 不支持 CancellationToken
            _client = await Task.Run(() => _listener.AcceptBluetoothClient(), ct);
            _stream = _client.GetStream();
            _connected = true;

            Log.I(Tag, $"Android connected: {_client.RemoteMachineName}");

            // 启动帧分割读取循环
            _readLoop = Task.Run(() => ReadLoopAsync(ct));
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.I(Tag, "Accept cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Log.E(Tag, $"Accept error: {ex.Message}");
            return false;
        }
    }

    /// <summary>ITransport.ConnectAsync — 蓝牙 Server 模式不使用此方法（由 WaitForConnectionAsync 替代）。</summary>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>断开当前客户端连接（不停止监听，可继续等待下一个连接）</summary>
    public async Task DisconnectAsync()
    {
        _connected = false;
        _cts?.Cancel();

        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
            _readLoop = null;
        }

        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;

        Log.I(Tag, "Client disconnected");
    }

    /// <summary>停止监听（关闭整个 Server，释放所有资源）</summary>
    public void StopListening()
    {
        _connected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        Log.I(Tag, "RFCOMM server stopped");
    }

    /// <summary>发送数据包到已连接的 Android 客户端</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_connected || _stream == null) return;

        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            Log.E(Tag, $"SendAsync error: {ex.Message}");
            _connected = false;
        }
    }

    // ── 流式帧分割读取循环 ──
    // RFCOMM 是字节流（无消息边界），利用 PacketHeader 的 PayloadLength 字段做帧分割：
    // 1. 读 15B header → 校验 Magic+Version → 解析 PayloadLength
    // 2. 再读 PayloadLength 字节 payload
    // 3. 组装完整包 → 触发 PacketReceived 事件

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[PacketHeaderSize];

        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                // 1. 读 15B header（精确读满）
                if (!await ReadExactAsync(headerBuf, PacketHeaderSize, ct))
                    break;

                // 2. 校验 Magic + Version，解析 PayloadLength
                //    注意：不能用 PacketHeader.TryDecode（它会校验 payloadLen == data.Length - 15，
    //    但此时只有 header 没有 payload，会返回 null）
                int magic = (headerBuf[0] << 24) | (headerBuf[1] << 16) | (headerBuf[2] << 8) | headerBuf[3];
                if (magic != 0x4C414242 || headerBuf[4] != 0x02)
                {
                    Log.W(Tag, "Invalid header received, disconnecting");
                    break;
                }

                int payloadLen = (headerBuf[11] << 24) | (headerBuf[12] << 16) | (headerBuf[13] << 8) | headerBuf[14];
                if (payloadLen < 0 || payloadLen > 65536)
                {
                    Log.W(Tag, $"Invalid payload length: {payloadLen}");
                    break;
                }

                // 3. 读 payload
                byte[] payload = Array.Empty<byte>();
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    if (!await ReadExactAsync(payload, payloadLen, ct))
                        break;
                }

                // 4. 组装完整包 → 触发事件
                var fullPacket = new byte[PacketHeaderSize + payloadLen];
                Array.Copy(headerBuf, 0, fullPacket, 0, PacketHeaderSize);
                if (payloadLen > 0)
                    Array.Copy(payload, 0, fullPacket, PacketHeaderSize, payloadLen);

                PacketReceived?.Invoke(fullPacket);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (IOException) { break; }  // 连接断开
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.E(Tag, $"ReadLoop error: {ex.Message}");
                break;
            }
        }

        _connected = false;
        Log.I(Tag, "ReadLoop exited");
    }

    /// <summary>精确读取 count 字节（流可能分片到达，循环读满）</summary>
    private async Task<bool> ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            ct.ThrowIfCancellationRequested();
            if (_stream == null) return false;

            int read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) return false;  // EOF
            offset += read;
        }
        return true;
    }

    private const int PacketHeaderSize = 15;

    public void Dispose()
    {
        StopListening();
    }
}
