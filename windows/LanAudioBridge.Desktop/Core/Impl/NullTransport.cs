using System;
using System.Threading;
using System.Threading.Tasks;

namespace LanAudioBridge.Core;

/// <summary>
/// NullTransport — ITransport 桩实现
/// 所有方法空操作，用于未连接时的安全占位。
/// </summary>
public class NullTransport : ITransport
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public bool IsConnected => false;
    public TransportType Type => TransportType.Udp;
}
