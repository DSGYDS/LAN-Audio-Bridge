using System;
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

    // ── 冷启动淡入（掩盖初始 HAL 冷启动卡顿） ──
    private int _coldStartFrame;            // 当前冷启动帧计数
    private const int ColdStartFadeFrames = 100; // 淡入时长：100 帧 ≈ 2 秒

    // ── 诊断计数 ──
    private long _pktRecvCount;      // 收到的 UDP 包总数
    private long _audioPktCount;     // AUDIO 类型包数
    private long _decodeOkCount;     // 解码成功数
    private long _decodeFailCount;   // 解码失败数
    private long _pullOkCount;       // JitterBuffer Pull 成功数
    private long _pullNullCount;     // JitterBuffer Pull 空（underrun）数
    private DateTime _diagLastLog = DateTime.UtcNow;

    // ── FEC 丢包补偿 ──
    private ushort _lastSeq;
    private bool _hasLastSeq;
    private long _lostFrames;
    private long _fecRecovered;

    // ── PLC 丢包隐藏（underrun 时用 Opus 解码器生成 comfort noise） ──
    private int _consecutivePlc;              // 连续 PLC 帧数
    private const int MaxPlcFrames = 25;      // 最多连续 25 帧 PLC（500ms），超过后回退静音

    // ── Jitter Buffer（抵消网络抖动，冷启动 HAL 帧突发需要更深预缓冲） ──
    private readonly JitterBuffer _jitterBuffer = new(capacity: 24, preFillCount: 5);
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
        // 注意：不在此处调用 _router.Start()！
        // 冷启动时若提前创建 WaveOutEvent 会“空跑”数秒，导致音频到达后播放一帧即停。
        // 扬声器在握手 SetMode 时才创建，确保设备启动即有数据流入。

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

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 2);
    }

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

    /// <summary>
    /// 重置会话状态（收到新 HELLO 时调用）。
    /// Android 每次 HELLO 后会重启推流（seq 从 0 开始），
    /// 必须重置 JitterBuffer 和 seq 追踪，否则新会话帧会被当作“迟到包”丢弃。
    /// </summary>
    public void ResetSession()
    {
        _jitterBuffer.Reset();
        _hasLastSeq = false;
        _lastSeq = 0;
        _frameCount = 0;
        _watchdogFired = false;
        _coldStartFrame = 0;  // 重置淡入
        _lastAudioTime = DateTime.UtcNow;  // 给新会话一个宽限期
        Log.I("AudioEngine", "Session reset (new HELLO)");
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

        Interlocked.Increment(ref _pktRecvCount);

        try
        {
            // ── 通过 IPacketProtocol 解码数据包 ──
            var packet = _protocol.Decode(data.Span);
            if (packet == null) return; // 校验失败，丢弃

            // 仅处理 AUDIO 类型包（握手包由 HandshakeServer 处理）
            if (packet.Value.Type != PacketType.Audio) return;
            Interlocked.Increment(ref _audioPktCount);

            var opusData = packet.Value.Payload;
            ushort seq = packet.Value.Sequence;

            // ── FEC 丢包补偿：检测 seq 跳号，用当前包的冗余数据恢复丢失帧 ──
            if (_hasLastSeq)
            {
                int gap = (ushort)(seq - _lastSeq);
                if (gap == 2) // 恰好丢了 1 帧 → 用 FEC 恢复
                {
                    var fecPcm = DecodeFec(opusData);
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

            var pcm = DecodeAndProcess(opusData);
            if (pcm != null)
            {
                Interlocked.Increment(ref _decodeOkCount);
                // 更新最后音频时间（看门狗恢复计时）
                _lastAudioTime = DateTime.UtcNow;
                _watchdogFired = false;

                // 推入 Jitter Buffer（由播放定时器匀速拉取）
                _jitterBuffer.Push(seq, pcm);
                if (Interlocked.Read(ref _frameCount) == 0)
                {
                    Log.I("AudioEngine", $"First decoded frame: seq={seq}, pcmLen={pcm.Length}");
                    OnFirstFrameDecoded?.Invoke();
                }
                Interlocked.Increment(ref _frameCount);
            }
            else
            {
                Interlocked.Increment(ref _decodeFailCount);
            }
        }
        catch (Exception ex)
        {
            Log.E("AudioEngine", $"OnPacketReceived error: {ex.Message}");
        }
    }

    // ── Opus 解码 + 音量缩放 ──
    private float[]? DecodeAndProcess(byte[] opus)
    {
        var pcmShort = DecodeShort(opus, false);
        if (pcmShort == null) return null;
        return ShortToFloat(pcmShort);
    }

    /// <summary>用当前包的 FEC 冗余数据恢复丢失的前一帧</summary>
    private float[]? DecodeFec(byte[] currentOpus)
    {
        try
        {
            var pcmShort = DecodeShort(currentOpus, true);
            if (pcmShort == null) return null;
            return ShortToFloat(pcmShort);
        }
        catch
        {
            return null; // FEC 恢复失败不影响正常流程
        }
    }

    /// <summary>Opus PLC（丢包隐藏）：用解码器内部状态生成 comfort noise，替代纯静音</summary>
    private float[]? DecodePlc()
    {
        try
        {
            var pcm = new short[FrameSize];
#pragma warning disable CS0618
            int n = _decoder.Decode(null, 0, 0, pcm, 0, FrameSize, false);
#pragma warning restore CS0618
            return n > 0 ? ShortToFloat(pcm) : null;
        }
        catch { return null; }
    }

    /// <summary>Opus 解码（decodeFEC=true 时用包内冗余数据恢复丢帧）</summary>
    private short[]? DecodeShort(byte[] opus, bool fec)
    {
        try
        {
            var pcm = new short[FrameSize];
#pragma warning disable CS0618
            int n = _decoder.Decode(opus, 0, opus.Length, pcm, 0, FrameSize, fec);
#pragma warning restore CS0618
            return n > 0 ? pcm : null;
        }
        catch (Exception ex)
        {
            Log.E("AudioEngine", $"Decode error (opus len={opus.Length}, fec={fec}): {ex.Message}");
            return null;
        }
    }

    /// <summary>short[] → float32 + 音量缩放（复用逻辑）</summary>
    private float[] ShortToFloat(short[] pcmShort)
    {
        var pcmFloat = new float[pcmShort.Length];
        float vol = _volume;
        for (int i = 0; i < pcmShort.Length; i++)
            pcmFloat[i] = pcmShort[i] / 32768f * vol;
        return pcmFloat;
    }

    // ── Jitter Buffer 播放定时器回调（每 20ms） ──
    // 从缓冲中匀速拉取一帧写入 AudioRouter，保证播放节奏稳定
    private void PlaybackTick()
    {
        if (!_running) return;

        var pcm = _jitterBuffer.Pull();
        if (pcm != null)
        {
            Interlocked.Increment(ref _pullOkCount);
            _consecutivePlc = 0;  // 成功拉取，重置 PLC 计数

            // 冷启动淡入：前 2 秒音量从 0 线性渐变到 1，掩盖 HAL 冷启动初始卡顿
            if (_coldStartFrame < ColdStartFadeFrames)
            {
                float gain = (float)(_coldStartFrame + 1) / ColdStartFadeFrames;
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] *= gain;
                _coldStartFrame++;
            }

            _router.OnAudioFrame(pcm);
        }
        else if (_jitterBuffer.IsPrefilled && _consecutivePlc < MaxPlcFrames)
        {
            // PLC：underrun 时用 Opus 解码器生成 comfort noise，听感优于纯静音
            Interlocked.Increment(ref _pullNullCount);
            _consecutivePlc++;
            var plc = DecodePlc();
            if (plc != null)
                _router.OnAudioFrame(plc);
        }
        else
        {
            Interlocked.Increment(ref _pullNullCount);
        }

        // 每 5 秒输出一次诊断统计
        if ((DateTime.UtcNow - _diagLastLog).TotalSeconds >= 5)
        {
            _diagLastLog = DateTime.UtcNow;
            Log.I("AudioEngine",
                $"[Diag] recv={Interlocked.Read(ref _pktRecvCount)} audio={Interlocked.Read(ref _audioPktCount)} " +
                $"decOk={Interlocked.Read(ref _decodeOkCount)} decFail={Interlocked.Read(ref _decodeFailCount)} " +
                $"pullOk={Interlocked.Read(ref _pullOkCount)} pullNull={Interlocked.Read(ref _pullNullCount)} " +
                $"jbCount={_jitterBuffer.Count} prefilled={_jitterBuffer.IsPrefilled} " +
                $"speakerReady={_router.IsSpeakerReady}");
        }
    }
}
