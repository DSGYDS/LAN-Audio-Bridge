using System;
using System.Threading;
using System.Threading.Tasks;

namespace LanAudioBridge.Core;

/// <summary>
/// ITransport — 统一网络传输接口
///
/// 所有链路（UDP / WiFi Direct / USB / 热点 / Relay）都实现此接口。
/// ConnectAsync 参数在构造时传入（host + port），无参。
/// </summary>
public interface ITransport
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>收到数据包时触发</summary>
    event Action<ReadOnlyMemory<byte>>? PacketReceived;

    /// <summary>当前是否已连接</summary>
    bool IsConnected { get; }

    /// <summary>传输类型</summary>
    TransportType Type { get; }
}
