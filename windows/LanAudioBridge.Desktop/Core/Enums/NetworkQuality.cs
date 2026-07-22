namespace LanAudioBridge.Core;

/// <summary>
/// 网络质量等级枚举
/// </summary>
public enum NetworkQuality
{
    /// <summary>未知</summary>
    Unknown,

    /// <summary>优秀（局域网低延迟）</summary>
    Excellent,

    /// <summary>良好（WiFi 已连接且互联网可达）</summary>
    Good,

    /// <summary>较差（仅蜂窝数据或信号弱）</summary>
    Poor,

    /// <summary>已断开</summary>
    Disconnected
}
