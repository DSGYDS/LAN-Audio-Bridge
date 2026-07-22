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
#pragma warning disable CS0067 // 桩实现无需触发事件
    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
#pragma warning restore CS0067
    public bool IsConnected => false;
    public TransportType Type => TransportType.Udp;
}
