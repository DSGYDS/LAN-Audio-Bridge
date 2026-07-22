namespace LanAudioBridge.Core;

/// <summary>
/// 采集源类型枚举
/// </summary>
public enum CapturerType
{
    /// <summary>麦克风采集</summary>
    Microphone,

    /// <summary>系统音频采集（MediaProjection / WASAPI Loopback）</summary>
    SystemAudio,

    /// <summary>混音（麦克风 + 系统音频）</summary>
    Mixed
}
