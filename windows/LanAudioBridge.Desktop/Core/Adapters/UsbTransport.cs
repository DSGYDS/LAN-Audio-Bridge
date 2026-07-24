using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// UsbTransport — ITransport 的 TCP 实现（Windows 端，Client 角色）
///
/// 职责：通过 ADB forward 隧道连接 Android TCP Server，
/// 在字节流上实现 PacketHeader 帧分割（15B header + payload）。
///
/// 数据流：
///   Windows App (TCP Client) → localhost:12348 → [adb forward] → Android:12348 (TCP Server)
///   注意：adb forward 命令会让 adb 进程监听 Windows 12348 端口，
///   因此 Windows 端不能再绑定此端口，只能作为 Client 连接。
///
/// 帧分割协议（与 BluetoothTransport 一致）：
///   发送：直接写入 [15B header + payload] 完整字节数组
///   接收：读 15B → 解析 PayloadLength → 再读 payload → 触发 PacketReceived
///
/// 依赖：USB 链路专属，与 LAN/P2P/蓝牙完全解耦。
/// </summary>
public sealed class UsbTransport : ITransport, IDisposable
{
    private const string Tag = "UsbTransport";

    /// <summary>USB 链路 TCP 端口（adb forward 双端一致）</summary>
    public const int Port = 12348;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private volatile bool _connected;

    // ── ITransport 实现 ──

    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public bool IsConnected => _connected;
    public TransportType Type => TransportType.Usb;

    /// <summary>
    /// 连接到 Android TCP Server（localhost:12348，通过 adb forward 隧道）。
    /// 需先由 UsbDeviceHelper 执行 adb forward 建立隧道。
    /// 连接成功后自动启动帧分割读取循环。
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync("127.0.0.1", Port, ct);
            _stream = _client.GetStream();
            _connected = true;

            _cts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

            Log.I(Tag, $"TCP connected to localhost:{Port} (via adb forward)");
        }
        catch (Exception ex)
        {
            Log.E(Tag, $"Connect failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    /// <summary>断开 TCP 连接，停止读取循环</summary>
    public async Task DisconnectAsync()
    {
        if (!_connected) return;
        _connected = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
            _readLoop = null;
        }

        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;

        Log.I(Tag, "TCP disconnected");
    }

    /// <summary>发送数据包到 Android（通过 adb forward 隧道）</summary>
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
    // TCP 是字节流（无消息边界），利用 PacketHeader 的 PayloadLength 字段做帧分割：
    // 1. 读 15B header → 校验 Magic+Version → 解析 PayloadLength
    // 2. 再读 PayloadLength 字节 payload
    // 3. 组装完整包 → 触发 PacketReceived 事件

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[HeaderSize];

        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                // 1. 读 15B header（精确读满）
                if (!await ReadExactAsync(headerBuf, HeaderSize, ct))
                    break;

                // 2. 校验 Magic + Version，解析 PayloadLength
                //    注意：不能用 PacketHeader.TryDecode（帧分割场景下仅传 header 会校验失败）
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
                var fullPacket = new byte[HeaderSize + payloadLen];
                Array.Copy(headerBuf, 0, fullPacket, 0, HeaderSize);
                if (payloadLen > 0)
                    Array.Copy(payload, 0, fullPacket, HeaderSize, payloadLen);

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

    private const int HeaderSize = 15;

    public void Dispose()
    {
        _connected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
    }
}
