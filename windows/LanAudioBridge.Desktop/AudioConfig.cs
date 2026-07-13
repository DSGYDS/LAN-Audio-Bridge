namespace LanAudioBridge.Desktop;

/// <summary>
/// AudioConfig — Windows 端音频参数集中配置
///
/// ## 职责
/// 统一管理所有音频相关常量，消除散落在各文件中的 hardcode。
/// AudioRouter 通过构造函数接收此配置。
///
/// ## 延迟参数说明（当前值，经实测稳定）
/// - BufferMs = 100 — 抖动缓冲，从 300 逐步降至 100，再降需监控 UNDERRUN
/// - WaveOutLatency = 100 — WaveOutEvent DesiredLatency，50 卡顿回退至此
/// - WasapiLatency = 50 — WasapiOut（CABLE Input），50 稳定的下限
///
/// ## 音频参数
/// - 采样率 48kHz / 16bit / 单声道 / 20ms 帧（960 采样点 / 1920 字节）
/// - Opus 解码对应 128kbps 编码流
/// </summary>
public sealed class AudioConfig
{
    /// <summary>采样率（Hz）</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>声道数（单声道）</summary>
    public int Channels { get; set; } = 1;

    /// <summary>每帧时长（ms）</summary>
    public int FrameMs { get; set; } = 20;

    /// <summary>UDP 音频端口</summary>
    public int AudioPort { get; set; } = 12345;

    // ── 缓冲/延迟参数 ──

    /// <summary>抖动缓冲时长（ms）</summary>
    public int BufferMs { get; set; } = 100;

    /// <summary>WaveOutEvent DesiredLatency（ms）</summary>
    public int WaveOutLatency { get; set; } = 100;

    /// <summary>WasapiOut CABLE Input DesiredLatency（ms）</summary>
    public int WasapiLatency { get; set; } = 50;

    /// <summary>音频看门狗超时（ms），超过此时间无音频包认为连接中断</summary>
    public int AudioTimeoutMs { get; set; } = 3000;

    // ── 派生常量 ──

    /// <summary>每帧采样点数</summary>
    public int FrameSize => SampleRate * FrameMs / 1000;

    /// <summary>每帧字节数（PCM16LE）</summary>
    public int FrameBytes => FrameSize * sizeof(short);

    /// <summary>默认配置</summary>
    public static readonly AudioConfig Default = new();
}
