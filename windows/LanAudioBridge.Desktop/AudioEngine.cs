using System;
using System.Buffers;
using System.Net;
using System.Threading;
using Concentus.Structs;
using NAudio.Wave;
using LanAudioBridge.Core;
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

    /// <summary>构造 AudioEngine，接收 AudioConfig 参数和 ITransport 实例</summary>
    public AudioEngine(ITransport? transport = null, AudioConfig? config = null)
    {
        _config = config ?? AudioConfig.Default;
        _router = new AudioRouter(_config);
        _transport = transport;
    }

    // ── 解码器 ──
#pragma warning disable CS0618 // OpusDecoder 标了 Obsolete，但当前 Concentus 无替代
    private readonly OpusDecoder _decoder = new(SampleRate, Channels);
#pragma warning restore CS0618

    // ── 路由器 ──
    private readonly AudioRouter _router;
    public AudioRouter Router => _router;

    // ── 网络（通过 ITransport 接口） ──
    private readonly ITransport? _transport;

    // ── 协议编解码（通过 IPacketProtocol 接口） ──
    private readonly IPacketProtocol _protocol = new LanAudioBridge.Core.Adapters.PacketHeaderAdapter();

    // ── 运行状态 ──
    private volatile bool _running;
    private long _frameCount;
    private float _volume = 1.0f;

    // ── FEC 丢包补偿 ──
    private ushort _lastSeq;
    private bool _hasLastSeq;
    private long _lostFrames;
    private long _fecRecovered;

    // ── Jitter Buffer（抵抚网络抖动） ──
    private readonly JitterBuffer _jitterBuffer = new(capacity: 5, preFillCount: 3);
    private Timer? _playbackTimer;  // 20ms 定时拉取帧

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

    // ── 生命周期 ──

    /// <summary>启动 UDP 监听 + 音频路由，Stop 后可重新 Start</summary>
    public void Start()
    {
        if (_running) return;

        if (_transport == null)
        {
            Log.E("AudioEngine", "No ITransport provided, cannot start.");
            return;
        }

        _running = true;
        Reset();
        _router.Start();

        // 启动音频看门狗（每 500ms 检测一次音频超时）
        _watchdogTimer = new Timer(_ => CheckWatchdog(), null, 500, 500);

        // 启动 Jitter Buffer 播放定时器（每 20ms 拉取一帧）
        _playbackTimer = new Timer(_ => PlaybackTick(), null, 20, 20);

        // 订阅 Transport 数据包事件，启动接收循环
        _transport.PacketReceived += OnPacketReceived;
        _ = _transport.ConnectAsync();
    }

    /// <summary>停止 UDP 监听 + 音频路由</summary>
    public void Stop()
    {
        _running = false;

        // 停止音频看门狗
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        // 停止 Jitter Buffer 播放定时器
        _playbackTimer?.Dispose();
        _playbackTimer = null;

        // 取消订阅并断开 Transport
        if (_transport != null)
        {
            _transport.PacketReceived -= OnPacketReceived;
            _ = _transport.DisconnectAsync();
        }

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
        _hasLastSeq = false;
        _lastSeq = 0;
        _lostFrames = 0;
        _fecRecovered = 0;
        _jitterBuffer.Reset();
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

            // 重置 JitterBuffer 和 seq 追踪，为新连接做准备
            _jitterBuffer.Reset();
            _hasLastSeq = false;
            _lastSeq = 0;

            OnAudioTimeout?.Invoke();
        }
    }

    // ── 数据包接收回调（由 ITransport.PacketReceived 事件触发） ──
    // 通过 IPacketProtocol 解码 → Opus 解码 → 路由分发
    private void OnPacketReceived(ReadOnlyMemory<byte> data)
    {
        if (!_running) return;

        try
        {
            // ── 通过 IPacketProtocol 解码数据包 ──
            var packet = _protocol.Decode(data.Span);
            if (packet == null) return; // 校验失败，丢弃

            // 仅处理 AUDIO 类型包（握手包由 HandshakeServer 处理）
            if (packet.Value.Type != PacketType.Audio) return;

            var opusData = packet.Value.Payload;
            ushort seq = packet.Value.Sequence;
            int opusLen = opusData.Length;
            byte[] opus = BytePool.Rent(opusLen);
            try
            {
                Buffer.BlockCopy(opusData, 0, opus, 0, opusLen);

                // ── FEC 丢包补偿：检测 seq 跳号，用当前包的冗余数据恢复丢失帧 ──
                if (_hasLastSeq)
                {
                    int gap = (ushort)(seq - _lastSeq);
                    if (gap == 2) // 恰好丢了 1 帧 → 用 FEC 恢复
                    {
                        var fecPcm = DecodeFec(opus.AsSpan(0, opusLen));
                        if (fecPcm != null)
                        {
                            _fecRecovered++;
                            _jitterBuffer.Push((ushort)(seq - 1), fecPcm);
                        }
                    }
                    else if (gap > 2 && gap < 1000) // 丢了多帧（排除新会话误判）
                    {
                        _lostFrames += gap - 1;
                    }
                    // gap >= 1000 视为新会话（Android 重新推流 seq 重置），不统计丢包
                }
                _lastSeq = seq;
                _hasLastSeq = true;

                var pcm = DecodeAndProcess(opus.AsSpan(0, opusLen));
                if (pcm != null)
                {
                    // 更新最后音频时间（看门狗恢复计时）
                    _lastAudioTime = DateTime.UtcNow;
                    _watchdogFired = false;

                    // 推入 Jitter Buffer（由播放定时器匀速拉取）
                    _jitterBuffer.Push(seq, pcm);
                    if (Interlocked.Read(ref _frameCount) == 0)
                    {
                        OnFirstFrameDecoded?.Invoke();
                    }
                    Interlocked.Increment(ref _frameCount);
                }
            }
            finally
            {
                BytePool.Return(opus);
            }
        }
        catch (Exception ex)
        {
            Log.E("AudioEngine", $"OnPacketReceived error: {ex.Message}");
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

    /// <summary>用当前包的 FEC 冗余数据恢复丢失的前一帧</summary>
    private float[]? DecodeFec(ReadOnlySpan<byte> currentOpus)
    {
        try
        {
            var pcm = new short[FrameSize];
#pragma warning disable CS0618
            // decodeFEC=true：告诉解码器用当前包内嵌的冗余数据恢复丢失帧
            int n = _decoder.Decode(currentOpus.ToArray(), 0, currentOpus.Length, pcm, 0, FrameSize, true);
#pragma warning restore CS0618
            if (n <= 0) return null;

            var pcmFloat = new float[n];
            for (int i = 0; i < n; i++)
                pcmFloat[i] = pcm[i] / 32768f * _volume;
            return pcmFloat;
        }
        catch
        {
            return null; // FEC 恢复失败不影响正常流程
        }
    }

    // ── Jitter Buffer 播放定时器回调（每 20ms） ──
    // 从缓冲中匀速拉取一帧写入 AudioRouter，保证播放节奏稳定
    private void PlaybackTick()
    {
        if (!_running) return;

        var pcm = _jitterBuffer.Pull();
        if (pcm != null)
        {
            _router.OnAudioFrame(pcm);
        }
        // pcm == null 表示 underrun，BufferedWaveProvider 会自动填充静音
    }
}
