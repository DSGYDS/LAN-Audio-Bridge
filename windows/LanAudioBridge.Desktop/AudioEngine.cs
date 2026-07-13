using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Concentus.Structs;
using NAudio.Wave;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 音频引擎 — UDP 接收 → Opus 解码 → AudioRouter 分发
/// 解码循环在独立后台线程运行
/// </summary>
public sealed class AudioEngine : IDisposable
{
    // ── 音频参数（已集中到 AudioConfig） ──
    private readonly AudioConfig _config;
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public int FrameSize => _config.FrameSize;
    public int FrameBytes => _config.FrameBytes;
    public int Port => _config.AudioPort;

    /// <summary>构造 AudioEngine，接收 AudioConfig 参数</summary>
    public AudioEngine(AudioConfig? config = null)
    {
        _config = config ?? AudioConfig.Default;
        _router = new AudioRouter(_config);
    }

    // ── 解码器 ──
#pragma warning disable CS0618 // OpusDecoder 标了 Obsolete，但当前 Concentus 无替代
    private readonly OpusDecoder _decoder = new(SampleRate, Channels);
#pragma warning restore CS0618

    // ── 路由器 ──
    private readonly AudioRouter _router;
    public AudioRouter Router => _router;

    // ── 网络 ──
    private UdpClient? _sock;
    private Thread? _recvThread;

    // ── 运行状态 ──
    private volatile bool _running;
    private long _frameCount;
    private float _volume = 1.0f;

    // ── 音频看门狗（断线检测） ──
    private DateTime _lastAudioTime = DateTime.MinValue;   // 最后收到音频帧的时间
    private volatile bool _watchdogFired;                   // 防止 watchog 重复触发
    private Timer? _watchdogTimer;                          // 每 500ms 检查一次
    private int AudioTimeoutMs => _config.AudioTimeoutMs;

    /// <summary>解码出第一帧音频时触发（仅一次），用于 ConnectionState 状态机</summary>
    public event Action? OnFirstFrameDecoded;

    /// <summary>音频超时触发（连续 [AudioTimeoutMs]ms 未收到音频），用于进入 RECOVERING</summary>
    public event Action? OnAudioTimeout;

    // ── 字节数组池（减少 GC 压力） ──
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    private const int RecvTimeoutMs = 1000;

    // ── 生命周期 ──

    /// <summary>启动 UDP 监听 + 音频路由，Stop 后可重新 Start</summary>
    public void Start()
    {
        if (_running) return;

        // 端口绑定冲突时自动重试（最多 5 次，间隔递增）
        const int maxRetries = 5;
        UdpClient? sock = null;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                sock = new UdpClient(Port);
                sock.Client.ReceiveTimeout = RecvTimeoutMs;
                break;
            }
            catch (Exception ex) when (retry < maxRetries - 1)
            {
                Log.E("AudioEngine", $"Port {Port} bind failed (attempt {retry + 1}): {ex.Message}");
                Thread.Sleep((retry + 1) * 500);
            }
        }

        if (sock == null)
        {
            Log.E("AudioEngine", $"Port {Port} bind failed after {maxRetries} attempts.");
            return;
        }

        _running = true;
        _sock = sock;
        Reset();
        _router.Start();

        // 启动音频看门狗（每 500ms 检测一次音频超时）
        _watchdogTimer = new Timer(_ => CheckWatchdog(), null, 500, 500);

        _recvThread = new Thread(RecvLoop)
        {
            IsBackground = true,
            Name = "audio-recv",
        };
        _recvThread.Start();
    }

    /// <summary>停止 UDP 监听 + 音频路由</summary>
    public void Stop()
    {
        _running = false;

        // 停止音频看门狗
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        _sock?.Close();
        _sock?.Dispose();
        _sock = null;

        if (_recvThread != null && _recvThread.IsAlive && !_recvThread.Join(500))
        {
            // 线程未正常退出，不阻塞
        }
        _recvThread = null;

        _router.Stop();
    }

    // ── 属性 ──

    /// <summary>音频路由器（可切换模式、查询状态）</summary>
    /// <summary>播放音量，0.0 ~ 2.0</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 2);
    }

    public bool IsRunning => _running;
    public long FrameCount => _frameCount;

    public void Dispose()
    {
        Stop();
        _router.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 内部 ──

    private void Reset()
    {
        _frameCount = 0;
        _lastAudioTime = DateTime.MinValue;
        _watchdogFired = false;
    }

    // ── 音频看门狗 ──
    // 每 500ms 由 _watchdogTimer 触发，检测连续 [AudioTimeoutMs]ms 未收到音频
    // 触发状态 → RECOVERING（不发起重连，等 Android 发 HELLO 恢复）
    private void CheckWatchdog()
    {
        if (!_running || _watchdogFired) return;

        if ((DateTime.UtcNow - _lastAudioTime).TotalMilliseconds > AudioTimeoutMs)
        {
            _watchdogFired = true;
            _frameCount = 0; // 重置帧计数，使 OnFirstFrameDecoded 能在恢复时重新触发
            OnAudioTimeout?.Invoke();
        }
    }

    // ── UDP 接收循环 ──
    // 阻塞等待数据 → 解析 14B PacketHeader → Opus 解码 → 路由分发
    private void RecvLoop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                var sock = _sock;
                if (sock == null) { Thread.Sleep(10); continue; }

                byte[]? data;
                try
                {
                    data = sock.Receive(ref ep);
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                if (data == null || data.Length < PacketHeader.HeaderSize) continue;

                // ── 解析 14B PacketHeader ──
                var info = PacketHeader.TryDecode(data);
                if (info == null) continue; // 校验失败，丢弃

                // 仅处理 AUDIO 类型包（握手包由 HandshakeServer 处理）
                if (info.Value.Type != (byte)PacketType.Audio) continue;

                int opusLen = info.Value.PayloadLen;
                byte[] opus = BytePool.Rent(opusLen);
                try
                {
                    Buffer.BlockCopy(data, PacketHeader.HeaderSize, opus, 0, opusLen);

                    var pcm = DecodeAndProcess(opus.AsSpan(0, opusLen));
                    if (pcm != null)
                    {
                        // 更新最后音频时间（看门狗恢复计时）
                        _lastAudioTime = DateTime.UtcNow;
                        _watchdogFired = false;

                        _router.OnAudioFrame(pcm);
                        if (_frameCount == 0)
                        {
                            OnFirstFrameDecoded?.Invoke();
                        }
                        _frameCount++;
                    }
                }
                finally
                {
                    BytePool.Return(opus);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Log.E("AudioEngine", $"RecvLoop error: {ex.Message}");
                Thread.Sleep(10);
            }
        }
    }

    // ── Opus 解码 + 音量缩放 ──
    // 注：7kHz LPF 已移除（改用 Android 端 200Hz HPF 解决底噪），详见 2026-07-09 决策
    private float[]? DecodeAndProcess(ReadOnlySpan<byte> opus)
    {
        var pcmShort = DecodeShort(opus);
        if (pcmShort == null) return null;

        // short[] → float32（NAudio 使用 float32 波形）
        var pcmFloat = new float[pcmShort.Length];
        for (int i = 0; i < pcmShort.Length; i++)
            pcmFloat[i] = pcmShort[i] / 32768f;

        // 音量缩放
        if (_volume < 0.999f)
        {
            for (int i = 0; i < pcmFloat.Length; i++)
                pcmFloat[i] *= _volume;
        }

        return pcmFloat;
    }

    private short[]? DecodeShort(ReadOnlySpan<byte> opus)
    {
        try
        {
            var pcm = new short[FrameSize];
#pragma warning disable CS0618
            int n = _decoder.Decode(opus.ToArray(), 0, opus.Length, pcm, 0, FrameSize, false);
#pragma warning restore CS0618
            return n > 0 ? pcm : null;
        }
        catch (Exception ex)
        {
            Log.E("AudioEngine", $"Decode error (opus len={opus.Length}): {ex.Message}");
            return null;
        }
    }
}
