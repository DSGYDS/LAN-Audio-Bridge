# 代码审查 — 第一批：核心基础设施

## 1. LinkManager

### Windows — `Links/LinkManager.cs`

```csharp
using System;
using System.Threading.Tasks;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// LinkManager — 纯路由器（~50 行）
///
/// 只做一件事：持有各链路实例，转发操作。
/// 不含任何链路实现代码。
/// </summary>
public sealed class LinkManager : IDisposable
{
    // ── 四级链路实例 ──
    private readonly WifiLanLink _wifiLan;
    private readonly WifiDirectLink _wifiDirect;
    private readonly BluetoothLink _bluetooth;
    private readonly UsbLink _usb;
    private readonly ConnectionStateManager _stateManager = new();

    // ── 公开属性 ──
    public WifiLanLink WifiLan => _wifiLan;
    public WifiDirectLink WifiDirect => _wifiDirect;
    public BluetoothLink Bluetooth => _bluetooth;
    public UsbLink Usb => _usb;
    public ConnectionStateManager StateManager => _stateManager;

    public float Volume
    {
        get => _wifiLan.Engine.Volume;
        set => _wifiLan.Engine.Volume = value;
    }

    public LinkManager()
    {
        _wifiLan = new WifiLanLink(_stateManager);
        _wifiDirect = new WifiDirectLink(_stateManager, HandleRoute);
        _bluetooth = new BluetoothLink(_stateManager);
        _usb = new UsbLink(_stateManager);

        // 蓝牙会话开始时暂停 LAN 引擎（避免看门狗冲突），结束时恢复
        _bluetooth.OnSessionStarted += () => _wifiLan.Engine.Stop();
        _bluetooth.OnSessionEnded += () => _wifiLan.Engine.Start();

        // USB 会话同理
        _usb.OnSessionStarted += () => _wifiLan.Engine.Stop();
        _usb.OnSessionEnded += () => _wifiLan.Engine.Start();
    }

    /// <summary>共享路由控制</summary>
    private bool HandleRoute(int route) => _wifiLan.HandleRoute(route);

    // ── 操作转发 ──
    public Task StartLanAsync() => _wifiLan.StartAsync();
    public Task StartP2pAsync() => _wifiDirect.StartAsync();
    public Task StopP2pAsync() => _wifiDirect.StopAsync();
    public Task StartBluetoothAsync() => _bluetooth.StartAsync();
    public Task StopBluetoothAsync() => _bluetooth.StopAsync();
    public Task StartUsbAsync() => _usb.StartAsync();
    public Task StopUsbAsync() => _usb.StopAsync();

    public bool IsP2pActive => _wifiDirect.IsActive;
    public bool IsBluetoothActive => _bluetooth.IsActive;
    public bool IsUsbActive => _usb.IsActive;

    public void Dispose()
    {
        _wifiLan.Dispose();
        _wifiDirect.Dispose();
        _bluetooth.Dispose();
        _usb.Dispose();
    }
}
```

### Android — `links/LinkManager.kt`

```kotlin
package com.lanbridge.app.links

import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.links.bluetooth.BluetoothLink
import com.lanbridge.app.links.usb.UsbLink
import com.lanbridge.app.links.wifidirect.WifiDirectLink
import com.lanbridge.app.links.wifilan.WifiLanLink
import com.lanbridge.app.net.LinkType

class LinkManager(
    context: Context,
    pipe: AudioPipeline,
    val stateManager: ConnectionStateManager
) {
    val wifiLan = WifiLanLink(context, pipe, stateManager)
    val wifiDirect = WifiDirectLink(context, pipe, stateManager)
    val bluetooth = BluetoothLink(context, pipe, stateManager)
    val usb = UsbLink(context, pipe, stateManager)

    private var activeLink: ILink? = null
    var lastLinkType: Byte = LinkType.WIFI_LAN
        private set

    suspend fun connect(linkType: Byte, params: LinkParams): Boolean {
        val link: ILink = when (linkType) {
            LinkType.WIFI_LAN -> wifiLan
            LinkType.WIFI_DIRECT -> wifiDirect
            LinkType.BLUETOOTH -> bluetooth
            LinkType.USB -> usb
            else -> return false
        }
        if (activeLink != null && activeLink !== link) {
            activeLink?.disconnect()
        }
        activeLink = link
        lastLinkType = linkType
        return link.connect(params)
    }

    suspend fun reconnect(params: LinkParams): Boolean =
        connect(lastLinkType, params)

    suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean =
        activeLink?.sendRouteUpdate(route, proj) ?: false

    fun disconnect() {
        activeLink?.disconnect()
        activeLink = null
    }

    val isStreaming: Boolean get() = activeLink?.isStreaming ?: false

    companion object {
        fun routeToCapture(r: Int): Int = when (r) {
            0, 3 -> AudioPipeline.MODE_SYSTEM
            1 -> AudioPipeline.MODE_MIX
            else -> AudioPipeline.MODE_MIC
        }
    }
}
```

---

## 2. ILink 接口

### Windows — `Links/ILink.cs`

```csharp
public interface ILink : IDisposable
{
    bool IsActive { get; }
    Task StartAsync();
    Task StopAsync();
}
```

### Android — `links/ILink.kt`

```kotlin
interface ILink {
    val isStreaming: Boolean
    var onStatusChanged: ((String) -> Unit)?
    var onStreamingChanged: ((Boolean) -> Unit)?
    suspend fun connect(params: LinkParams): Boolean
    suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean
    fun disconnect()
}
```

---

## 3. ITransport 接口

### Windows — `Core/Interfaces/ITransport.cs`

```csharp
public interface ITransport
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    event Action<ReadOnlyMemory<byte>>? PacketReceived;
    bool IsConnected { get; }
    TransportType Type { get; }
}
```

### Android — `core/interfaces/ITransport.kt`

```kotlin
interface ITransport {
    suspend fun connect()
    suspend fun disconnect()
    suspend fun send(data: ByteArray)
    var onPacketReceived: ((ByteArray) -> Unit)?
    val isConnected: Boolean
    val type: TransportType
}
```

---

## 4. IDataChannel 接口

### Windows — `Core/Interfaces/IDataChannel.cs`

```csharp
public interface IDataChannel
{
    string ChannelId { get; }
    StreamType StreamType { get; }
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    Task CloseAsync();
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default);
    void RegisterHandler(IStreamHandler handler);
    void UnregisterHandler();
    event Action<bool>? OnStateChanged;
}
```

### Android — `core/interfaces/IDataChannel.kt`

```kotlin
interface IDataChannel {
    val channelId: String
    val streamType: StreamType
    val isOpen: Boolean
    suspend fun open()
    suspend fun close()
    suspend fun send(data: ByteArray)
    suspend fun receive(): ByteArray
    fun registerHandler(handler: IStreamHandler)
    fun unregisterHandler()
    var onStateChanged: ((Boolean) -> Unit)?
}
```

---

## 5. IPacketProtocol 接口

### Windows — `Core/Interfaces/IPacketProtocol.cs`

```csharp
public struct Packet
{
    public PacketType Type;
    public byte LinkType;
    public ushort Sequence;
    public byte[] Payload;
}

public interface IPacketProtocol
{
    byte[] Encode(in Packet packet);
    Packet? Decode(ReadOnlySpan<byte> data);
}
```

### Android — `core/interfaces/IPacketProtocol.kt`

```kotlin
data class Packet(
    val type: PacketType,
    val linkType: Byte = LinkType.WIFI_LAN,
    val sequence: UShort,
    val payload: ByteArray
)

interface IPacketProtocol {
    fun encode(packet: Packet): ByteArray
    fun decode(data: ByteArray): Packet?
}
```
