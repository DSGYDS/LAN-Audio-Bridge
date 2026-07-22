namespace LanAudioBridge.Core;

/// <summary>
/// 流类型枚举 — 标识数据通道中承载的流种类
///
/// 当前仅 Audio 在用，其余为预留扩展。
/// </summary>
public enum StreamType
{
    /// <summary>音频流（当前唯一在用）</summary>
    Audio,

    /// <summary>视频流（预留：投屏）</summary>
    Video,

    /// <summary>文件流（预留：文件传输）</summary>
    File,

    /// <summary>控制流（预留：剪切板/远程指令）</summary>
    Control
}
