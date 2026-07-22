using System;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 连接状态枚举 — 用户可见的连接过程
///
/// 与 AudioEngine._running 是两套独立维度：
/// _running 是内部生命周期标志，这里是 UI 观测层
/// 两套并存，互不取代
///
/// ## 迁移规则
///   Idle ──→ Searching ──→ Found ──→ Connecting ──→ Connected ──→ Streaming
///     ↑         ↑                                           ↓            ↓
///     │         └───────────────────────────────── Reconnecting ──────────┘
///     │                                                      ↓
///     └──────────────── Disconnected ←──────────────────────┘
///
///   Error ← 任意状态（除 Disconnected 外）
///   Disconnected ← 任意状态
/// </summary>
public enum ConnectionState
{
    Idle,          // 初始就绪，等待用户操作
    Disconnected,  // 连接中断（物理断开 / 用户终止）
    Searching,     // 正在扫描局域网设备
    Found,         // 发现手机
    Connecting,    // 正在握手连接
    Connected,     // 握手成功
    Streaming,     // 音频正在传输中
    Reconnecting,  // 连接恢复中
    Error          // 确定性错误（端口绑定失败等）
}

/// <summary>连接状态中文标签映射</summary>
public static class ConnectionStateExtensions
{
    public static string ToChineseLabel(this ConnectionState state) => state switch
    {
        ConnectionState.Idle => "就绪",
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Searching => "正在寻找手机",
        ConnectionState.Found => "已发现设备",
        ConnectionState.Connecting => "正在连接",
        ConnectionState.Connected => "已连接",
        ConnectionState.Streaming => "音频传输中",
        ConnectionState.Reconnecting => "连接恢复中",
        ConnectionState.Error => "连接失败",
        _ => "未知状态"
    };
}

/// <summary>
/// 连接状态管理器 — 旁路观测层
/// 维护状态、校验合法转换、reason 追踪，通过事件通知 UI
/// 不替代任何现有生命周期标志
/// 对齐 P3 统一状态机规则
/// </summary>
public class ConnectionStateManager
{
    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    /// <summary>上次状态变化的原因（用于 UI 显示错误/恢复信息）</summary>
    public string? LastReason { get; private set; }

    /// <summary>状态变更事件，UI 订阅此事件更新显示</summary>
    public event Action<ConnectionState>? OnStateChanged;

    /// <summary>更新状态，相同状态不触发事件。校验合法转换，非法转换静默忽略。</summary>
    public void Update(ConnectionState newState, string? reason = null)
    {
        if (State == newState) return;
        if (!IsValidTransition(newState)) return;

        State = newState;
        if (reason != null) LastReason = reason;
        OnStateChanged?.Invoke(newState);
    }

    /// <summary>清空原因（用户重新开始连接时调用）</summary>
    public void ClearLastReason() => LastReason = null;

    /// <summary>检查 from → to 是否为合法转换（对齐 P3 统一状态机）</summary>
    private bool IsValidTransition(ConnectionState to)
    {
        return State switch
        {
            ConnectionState.Idle => to is ConnectionState.Searching or ConnectionState.Connecting or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Disconnected => to is ConnectionState.Searching or ConnectionState.Connecting or ConnectionState.Idle or ConnectionState.Error,
            ConnectionState.Searching => to is ConnectionState.Found or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Found => to is ConnectionState.Connecting or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Connecting => to is ConnectionState.Connected or ConnectionState.Error or ConnectionState.Disconnected,
            ConnectionState.Connected => to is ConnectionState.Streaming or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Streaming => to is ConnectionState.Reconnecting or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Reconnecting => to is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Searching or ConnectionState.Disconnected or ConnectionState.Error,
            ConnectionState.Error => to is ConnectionState.Disconnected or ConnectionState.Idle,
            _ => false
        };
    }
}
