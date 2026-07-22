using System;
using System.Buffers;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 音频路由 — 将解码后的 PCM 分发到扬声器 / 虚拟麦克风。
///
/// 模式映射：
///   SpeakerOnly (模式1): 系统音频 → 扬声器
///   MicOnly     (模式2): 手机麦克风 → 虚拟麦克风
///   Both        (模式3): 混音 → 扬声器 + 虚拟麦克风
///   MicOnlySys  (模式4): 系统音频 → 虚拟麦克风
///
/// 线程安全：OnAudioFrame 可在任意线程调用；内部使用锁协调模式切换。
/// </summary>
public sealed class AudioRouter : IDisposable
{
    public enum RouteMode
    {
        SpeakerOnly,
        MicOnly,
        Both,
        MicOnlySys,
    }

    // ── 音频参数（已集中到 AudioConfig） ──
    private readonly AudioConfig _config;
    private static readonly WaveFormat WaveFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    private const int FadeMs = 50;                   // 淡入时长（ms），防切模式时的爆音
    private const int FadeSamplesTotal = 48000 * FadeMs / 1000; // 2400 采样点
    private const float SpeakerGain = 1.0f;           // 扬声器增益（已从 1.8 降至 1.0 消除削波）

    // 复用枚举器，避免频繁创建 COM 对象
    private static readonly MMDeviceEnumerator DeviceEnumerator = new();

    // ── 音频输出 ──
    private IWavePlayer? _speakerOut;                 // 扬声器（WaveOutEvent）
    private BufferedWaveProvider? _speakerBuf;
    private IWavePlayer? _micOut;                     // 虚拟麦克风 CABLE Input（WasapiOut）
    private BufferedWaveProvider? _micBuf;

    // ── 运行时状态 ──
    private RouteMode _mode = RouteMode.SpeakerOnly;
    private readonly object _lock = new();            // 模式切换用锁
    private bool _disposed;
    private int _fadeSamplePos;                       // 淡入累计位置（跨帧）

    // 复用增益缓冲区，减少 GC
    private float[]? _gainScratch;

    // 复用字节池，减少 GC（每帧 ~3840 字节）
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    // ── 缓冲水位监控（用于延迟调优调试） ──

    /// <summary>扬声器缓冲水位统计快照</summary>
    public sealed class BufferStats
    {
        public double CurrentMs { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double AvgMs { get; set; }
        public int SampleCount { get; set; }
    }

    private BufferStats _speakerStats = new();
    private double _prevBufMs;
    private int _monitorFrameCount;

    private const int MonitorInterval = 50; // 每 50 帧（≈1s）更新一次统计

    /// <summary>获取当前扬声器缓冲水位统计，用于延迟监控</summary>
    public BufferStats SpeakerBufferStats => _speakerStats;

    /// <summary>麦克风输出状态变化时触发</summary>
    public Action<bool>? OnMicOutputChanged;
    /// <summary>错误通知</summary>
    public Action<string>? OnError;

    public RouteMode Mode => _mode;
    public bool IsOutputToMic => _mode is RouteMode.MicOnly or RouteMode.Both or RouteMode.MicOnlySys;

    // ── 生命周期 ──

    /// <summary>构造 AudioRouter，接收 AudioConfig 参数</summary>
    public AudioRouter(AudioConfig? config = null)
    {
        _config = config ?? AudioConfig.Default;
    }

    /// <summary>启动扬声器输出（默认 SpeakerOnly 模式）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_speakerOut != null) return;
            StartSpeaker();
        }
    }

    /// <summary>
    /// 切换路由模式。先停全部输出，再启动需要的（stop-start）。
    /// 返回 false 表示被拒绝（如缺少 CABLE 设备）。
    /// </summary>
    public bool SetMode(RouteMode newMode)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_mode == newMode && OutputsReady(newMode)) return true; // 同模式且设备就绪，无需操作
            // 同模式但设备未就绪（如被外部 Stop 过）→ 继续执行完整 stop-start 重启流程

            // 切到虚拟麦克风模式前先确认 CABLE 设备存在
            if (newMode is RouteMode.MicOnly or RouteMode.MicOnlySys or RouteMode.Both)
            {
                if (FindCableDevice() == null)
                {
                    OnError?.Invoke("VB-Audio Virtual Cable 未安装，请在 https://vb-audio.com/Cable/ 下载安装");
                    return false;
                }
            }

            var oldMode = _mode;
            bool wasMic = IsOutputToMic;

            // stop-start：先停全部，再启需要的
            StopSpeaker();
            StopMic();

            bool started = newMode switch
            {
                RouteMode.SpeakerOnly => EnsureSpeaker(),
                RouteMode.MicOnly => EnsureMic(),
                RouteMode.Both => EnsureSpeaker() && EnsureMic(),
                RouteMode.MicOnlySys => EnsureMic(),
                _ => false,
            };

            if (!started)
            {
                StopSpeaker();
                StopMic();
                _mode = oldMode;
                RestoreMode(oldMode);
                return false;
            }

            _mode = newMode;
            bool nowMic = IsOutputToMic;
            _fadeSamplePos = 0;

            if (wasMic != nowMic)
                OnMicOutputChanged?.Invoke(nowMic);

            return true;
        }
    }

    /// <summary>
    /// 接收一帧解码后的 PCM（float32 mono），由 AudioEngine 调用。线程安全。
    /// 根据当前模式路由到扬声器 / 虚拟麦克风 / 或同时输出
    /// </summary>
    public void OnAudioFrame(float[] pcm)
    {
        bool toSpeaker;
        bool toMic;

        // 只锁模式判断，不做计算（减少锁竞争）
        lock (_lock)
        {
            if (_disposed) return;
            toSpeaker = _mode is RouteMode.SpeakerOnly or RouteMode.Both;
            toMic = IsOutputToMic;
            if (!toSpeaker && !toMic) return;
        }

        // ── 淡入处理 ──
        // 切模式后第一个 50ms（2400 采样点）渐进淡入，防爆音
        var processed = pcm;
        int fadePos;
        lock (_lock) { fadePos = _fadeSamplePos; }

        if (fadePos < FadeSamplesTotal)
        {
            int interleave = Math.Min(FadeSamplesTotal - fadePos, pcm.Length);
            processed = ApplyFade(pcm, fadePos, interleave);
            lock (_lock) { _fadeSamplePos += interleave; }
        }

        if (toSpeaker)
        {
            var gained = ApplyGain(processed);   // 扬声器增益
            WriteToBuffer(_speakerBuf, gained);
        }

        if (toMic)
        {
            WriteToBuffer(_micBuf, processed);
        }

        OnFrameProcessed();  // 更新缓冲水位统计
    }

    // ── 缓冲水位统计（每帧调用） ──

    private void OnFrameProcessed()
    {
        var buf = _speakerBuf;
        if (buf == null) return;

        _monitorFrameCount++;
        if (_monitorFrameCount % MonitorInterval != 0) return;

        var ms = buf.BufferedDuration.TotalMilliseconds;
        var bytes = buf.BufferedBytes;

        // 更新统计
        if (_speakerStats.SampleCount == 0)
        {
            _speakerStats.MinMs = ms;
            _speakerStats.MaxMs = ms;
        }
        else
        {
            if (ms < _speakerStats.MinMs) _speakerStats.MinMs = ms;
            if (ms > _speakerStats.MaxMs) _speakerStats.MaxMs = ms;
        }
        _speakerStats.CurrentMs = ms;
        _speakerStats.AvgMs = ((_speakerStats.AvgMs * _speakerStats.SampleCount) + ms) / (_speakerStats.SampleCount + 1);
        _speakerStats.SampleCount++;

        // 异常日志：只在状态异常或突变时输出
        var delta = Math.Abs(ms - _prevBufMs);
        var shouldLog = false;
        var reason = "";

        if (ms < 20)                              { shouldLog = true; reason = "UNDERRUN"; }
        else if (ms > _config.BufferMs * 0.8)             { shouldLog = true; reason = "NEAR_FULL"; }
        else if (delta > 30 && _prevBufMs > 0)    { shouldLog = true; reason = $"JUMP {delta:F0}ms"; }

        if (shouldLog)
        {
            Log.I("AudioRouter",
                $"[BufMonitor] {reason} | " +
                $"Cur={ms:F0}ms Min={_speakerStats.MinMs:F0}ms " +
                $"Max={_speakerStats.MaxMs:F0}ms Avg={_speakerStats.AvgMs:F0}ms " +
                $"Bytes={bytes}");
        }

        _prevBufMs = ms;
    }

    /// <summary>停止所有输出。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopSpeaker();
            StopMic();
            _fadeSamplePos = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    // ── 淡入 ──

    private static float[] ApplyFade(float[] pcm, int fadePos, int fadeLen)
    {
        var result = new float[pcm.Length];
        for (int i = 0; i < fadeLen; i++)
        {
            // 淡入系数从 0→1（考虑跨帧累计位置）
            float t = (fadePos + i) / (float)FadeSamplesTotal;
            result[i] = pcm[i] * t;
        }
        for (int i = fadeLen; i < pcm.Length; i++)
            result[i] = pcm[i];
        return result;
    }

    // ── 增益 ──

    private float[] ApplyGain(float[] pcm)
    {
        if (Math.Abs(SpeakerGain - 1.0f) < 0.001f) return pcm;

        int len = pcm.Length;
        if (_gainScratch == null || _gainScratch.Length < len)
            _gainScratch = new float[len];

        for (int i = 0; i < len; i++)
        {
            float s = pcm[i] * SpeakerGain;
            if (s > 1.0f) s = 1.0f;
            else if (s < -1.0f) s = -1.0f;
            _gainScratch[i] = s;
        }
        return _gainScratch;
    }

    // ── 输出设备管理 ──

    private bool EnsureSpeaker()
    {
        if (_speakerOut != null) return true;
        return StartSpeaker();
    }

    private bool EnsureMic()
    {
        if (_micOut != null) return true;
        return StartMic();
    }

    private bool OutputsReady(RouteMode mode)
    {
        return mode switch
        {
            RouteMode.SpeakerOnly => _speakerOut != null,
            RouteMode.MicOnly => _micOut != null,
            RouteMode.Both => _speakerOut != null && _micOut != null,
            RouteMode.MicOnlySys => _micOut != null,
            _ => false,
        };
    }

    private void RestoreMode(RouteMode mode)
    {
        switch (mode)
        {
            case RouteMode.SpeakerOnly:
                EnsureSpeaker();
                break;
            case RouteMode.MicOnly:
            case RouteMode.MicOnlySys:
                EnsureMic();
                break;
            case RouteMode.Both:
                EnsureSpeaker();
                EnsureMic();
                break;
        }
    }

    private bool StartSpeaker()
    {
        try
        {
            _speakerBuf = new BufferedWaveProvider(WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(_config.BufferMs),
                DiscardOnBufferOverflow = true,
            };

            _speakerOut = new WaveOutEvent { DesiredLatency = _config.WaveOutLatency };
            _speakerOut.Init(_speakerBuf);
            _speakerOut.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.E("AudioRouter", $"Speaker init failed: {ex.Message}");
            StopSpeaker();
            OnError?.Invoke($"扬声器输出初始化失败: {ex.Message}");
            return false;
        }
    }

    private bool StartMic()
    {
        var cableDevice = FindCableDevice();
        if (cableDevice == null)
        {
            OnError?.Invoke("VB-Audio Virtual Cable 未安装，请在 https://vb-audio.com/Cable/ 下载安装");
            return false;
        }

        try
        {
            _micBuf = new BufferedWaveProvider(WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(_config.BufferMs),
                DiscardOnBufferOverflow = true,
            };

            _micOut = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, _config.WasapiLatency);
            _micOut.Init(_micBuf);
            _micOut.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.E("AudioRouter", $"CABLE WasapiOut init failed: {ex.Message}");
            StopMic();
            OnError?.Invoke($"CABLE Input 打开失败: {ex.Message}");
            return false;
        }
    }

    private void StopSpeaker()
    {
        if (_speakerOut != null)
        {
            _speakerOut.Stop();
            _speakerOut.Dispose();
            _speakerOut = null;
        }
        _speakerBuf = null;
    }

    private void StopMic()
    {
        if (_micOut != null)
        {
            _micOut.Stop();
            _micOut.Dispose();
            _micOut = null;
        }
        _micBuf = null;
    }

    // ── buffer 写入（使用 ArrayPool 减少分配） ──

    private static void WriteToBuffer(BufferedWaveProvider? buf, float[] pcm)
    {
        if (buf == null) return;

        int byteLen = pcm.Length * sizeof(float);
        byte[] pooled = BytePool.Rent(byteLen);
        try
        {
            Buffer.BlockCopy(pcm, 0, pooled, 0, byteLen);
            buf.AddSamples(pooled, 0, byteLen);
        }
        finally
        {
            BytePool.Return(pooled);
        }
    }

    // ── 设备查找 ──

    /// <summary>
    /// 查找 CABLE Input 设备。优先精确匹配 "CABLE Input"，
    /// 回退到模糊匹配包含 "CABLE"/"VB-Audio"/"Virtual Cable" 的设备。
    /// </summary>
    private static MMDevice? FindCableDevice()
    {
        var devices = DeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var dev in devices)
        {
            if (dev.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        foreach (var dev in devices)
        {
            var name = dev.FriendlyName;
            if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        return null;
    }
}
