using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core;

/// <summary>
/// IAudioRenderer — 统一音频渲染接口
///
/// 提供 PCM 到扬声器/虚拟麦克风的输出。
/// 不包含路由逻辑（路由是业务层 AudioRouter 的职责）。
///
/// 当前实现：
///   Windows — SpeakerRenderer（WaveOutEvent 扬声器）/ CableRenderer（WasapiOut CABLE Input）
///   Android — StubRenderer（Android 是纯发送端，不渲染）
/// </summary>
public interface IAudioRenderer
{
    /// <summary>准备渲染器（分配缓冲和设备）</summary>
    bool Prepare(AudioConfig config);

    /// <summary>开始播放</summary>
    void Play();

    /// <summary>停止播放</summary>
    void Stop();

    /// <summary>设置音量（0.0 ~ 1.0）</summary>
    void SetVolume(float volume);

    /// <summary>静音/取消静音</summary>
    void Mute(bool muted);

    /// <summary>喂入一帧 PCM 数据（IEEE Float32 little-endian）</summary>
    void FeedPcm(byte[] data, int offset, int count);

    /// <summary>释放所有资源</summary>
    void Release();

    /// <summary>是否已就绪（设备已打开）</summary>
    bool IsReady { get; }
}
