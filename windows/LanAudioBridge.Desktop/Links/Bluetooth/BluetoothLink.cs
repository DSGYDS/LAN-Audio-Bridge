using System;
using System.Threading;
using System.Threading.Tasks;
using LanAudioBridge.Core;
using LanAudioBridge.Core.Adapters;
using LanAudioBridge.Core.Factory;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop.Links;

/// <summary>
/// BluetoothLink — 蓝牙 RFCOMM 链路（常驻服务）
///
/// 职责：常驻监听 RFCOMM → 等待 Android 连接 → 被动接收 HELLO 回 ACK → AudioEngine 播放。
/// 与 WifiLanLink 对称：开机即启动，始终等待手机连接。
///
/// 数据通路：BluetoothTransport.PacketReceived → AudioEngine（Opus 解码 → 播放）
/// 握手方向：Android 发 HELLO(token) → Windows 校验 → 回 HELLO_ACK(route)（与 LAN 一致）
/// </summary>
public sealed class BluetoothLink : ILink
{
    private const string Tag = "BluetoothLink";

    // ── 链路常量 ──
    public const byte LinkTypeId = 0x03;

    // ── 核心模块 ──
    private BluetoothTransport? _transport;
    private AudioEngine? _engine;
    private readonly ConnectionStateManager _stateManager;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private volatile bool _started;

    // ── 事件（LinkManager / UI 订阅） ──
    public Action<string>? OnStatusChanged;
    public Action<bool>? OnActiveChanged;
    /// <summary>蓝牙会话开始（手机连接+握手成功），LinkManager 用于暂停 LAN 引擎</summary>
    public Action? OnSessionStarted;
    /// <summary>蓝牙会话结束（手机断开），LinkManager 用于恢复 LAN 引擎</summary>
    public Action? OnSessionEnded;

    public bool IsActive => _started;

    public BluetoothLink(ConnectionStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    // ── ILink 实现 ──

    /// <summary>启动蓝牙链路（常驻监听，与 LAN 一起开机启动）</summary>
    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 启动常驻监听循环（后台）
        _listenLoop = Task.Run(() => ListenLoopAsync(ct));

        OnStatusChanged?.Invoke("蓝牙：就绪，等待手机连接");
        OnActiveChanged?.Invoke(true);
        Log.I(Tag, "Bluetooth link started (resident)");
        return Task.CompletedTask;
    }

    /// <summary>停止蓝牙链路（关闭监听 + 断开当前连接）</summary>
    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_listenLoop != null)
        {
            try { await _listenLoop; } catch { }
            _listenLoop = null;
        }

        await CleanupSessionAsync();
        _transport?.StopListening();
        _transport?.Dispose();
        _transport = null;

        OnStatusChanged?.Invoke("蓝牙：已停止");
        OnActiveChanged?.Invoke(false);
    }

    // ── 常驻监听循环（核心状态机） ──
    // 流程：等待连接 → 被动握手 → 创建引擎播放 → 等待断开 → 清理 → 循环

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        _transport = new BluetoothTransport();
        _transport.StartListening();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 等待 Android 连接
                OnStatusChanged?.Invoke("蓝牙：等待手机连接...");
                var connected = await _transport.WaitForConnectionAsync(ct);
                if (!connected) continue;

                // 连接建立 → 等待 HELLO
                OnStatusChanged?.Invoke("蓝牙：手机已连接，等待握手...");
                var route = await BtPassiveHandshake.WaitForHelloAsync(_transport, ct);
                if (route < 0)
                {
                    OnStatusChanged?.Invoke("蓝牙：握手失败，重新等待...");
                    await _transport.DisconnectAsync();
                    continue;
                }

                // 握手成功 → 创建 AudioEngine → 播放
                OnSessionStarted?.Invoke();
                StartAudioEngine(route);
                OnStatusChanged?.Invoke($"蓝牙：推流中 ✓ 路线{route + 1}");

                // 监听 ROUTE 热切包（AudioEngine 只处理 Audio 包，ROUTE 由此处理）
                _transport.PacketReceived += OnRoutePacket;

                // 等待连接断开（ReadLoop 退出 = 连接断开）
                await WaitForDisconnectAsync(ct);

                // 断开 → 清理 → 重新等待
                _transport.PacketReceived -= OnRoutePacket;
                OnSessionEnded?.Invoke();
                OnStatusChanged?.Invoke("蓝牙：手机断开，重新等待...");
                CleanupAudioEngine();
                await _transport.DisconnectAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.E(Tag, $"ListenLoop error: {ex.Message}");
                if (!ct.IsCancellationRequested)
                    await Task.Delay(2000, ct);  // 防止快速循环
            }
        }
    }

    // ── ROUTE 热切（命名方法，确保可取消订阅） ──
    // 推流中手机切换路线时，发送 ROUTE 包，此处解码并切换 AudioRouter 模式

    private void OnRoutePacket(ReadOnlyMemory<byte> data)
        => BtPassiveHandshake.HandleRoutePacket(data, _engine, OnStatusChanged);

    // ── AudioEngine 管理（蓝牙专属引擎，不复用 LAN 引擎） ──

    /// <summary>创建蓝牙专属 AudioEngine，设置路由模式并启动播放</summary>

    private void StartAudioEngine(int route)
    {
        var speaker = PlatformFactory.CreateRenderer(useCable: false);
        var cable = PlatformFactory.CreateRenderer(useCable: true);
        _engine = new AudioEngine(_transport, speaker, cable);
        _engine.Router.SetMode(BtPassiveHandshake.RouteToMode(route));

        _engine.OnFirstFrameDecoded += () => _stateManager.Update(ConnectionState.Streaming);
        _engine.Start();
        _stateManager.Update(ConnectionState.Connected);
    }

    /// <summary>停止并释放 AudioEngine</summary>
    private void CleanupAudioEngine()
    {
        _engine?.Stop();
        _engine?.Dispose();
        _engine = null;
    }

    /// <summary>轮询等待 RFCOMM 连接断开（ReadLoop 退出后 IsConnected 变 false）</summary>
    private async Task WaitForDisconnectAsync(CancellationToken ct)
    {
        // 等待 transport 连接断开（IsConnected 变 false）
        while (!ct.IsCancellationRequested && _transport?.IsConnected == true)
            await Task.Delay(500, ct);
    }

    /// <summary>清理当前会话（引擎 + 传输层）</summary>
    private async Task CleanupSessionAsync()
    {
        CleanupAudioEngine();
        if (_transport != null)
            await _transport.DisconnectAsync();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _engine?.Dispose();
        _transport?.Dispose();
    }
}
