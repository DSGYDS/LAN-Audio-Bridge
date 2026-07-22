using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core;

/// <summary>
/// IAudioCapturer — 统一音频采集接口（纯 PCM 层）
///
/// 采集原始 PCM16LE 帧，不涉及 Opus 编解码。
/// 编解码由应用层 Pipeline 处理。
///
/// 当前实现：
///   Android — MicCapturerAdapter / SystemAudioCapturerAdapter
///   Windows — StubCapturer（Windows 是纯接收端，不采集）
/// </summary>
public interface IAudioCapturer
{
    /// <summary>准备采集器（分配资源）</summary>
    bool Prepare(AudioConfig config);

    /// <summary>开始采集</summary>
    bool Start();

    /// <summary>读取一帧 PCM16LE，返回实际读取的字节数（0 表示无数据）</summary>
    int ReadFrame(byte[] buffer, int offset, int count);

    /// <summary>停止采集</summary>
    void Stop();

    /// <summary>释放所有资源</summary>
    void Release();

    /// <summary>HAL 预热（丢弃前几帧，消除冷启动抖动）</summary>
    void Warmup() { }

    /// <summary>看门狗触发时重建采集器（默认返回 false 表示不支持）</summary>
    bool Restart() => false;

    /// <summary>采集源类型</summary>
    CapturerType SourceType { get; }
}
