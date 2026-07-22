using System;
using System.Threading;

namespace LanAudioBridge.Desktop;

/// <summary>
/// AudioWatchdog — 音频看门狗
///
/// 职责：500ms 定时检测音频超时，触发 OnTimeout 回调。
/// 不关心解码/路由，只关心"有没有音频在流"。
/// </summary>
public sealed class AudioWatchdog : IDisposable
{
    private readonly int _timeoutMs;
    private Timer? _timer;
    private DateTime _lastAudioTime = DateTime.MinValue;
    private volatile bool _fired;
    private volatile bool _running;

    /// <summary>音频超时触发（连续 timeoutMs 未收到音频）</summary>
    public event Action? OnTimeout;

    public AudioWatchdog(int timeoutMs)
    {
        _timeoutMs = timeoutMs;
    }

    public void Start()
    {
        _running = true;
        _fired = false;
        _lastAudioTime = DateTime.UtcNow;
        _timer = new Timer(_ => Check(), null, 500, 500);
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>每收到一帧有效音频时调用，重置计时</summary>
    public void NotifyAudioReceived()
    {
        _lastAudioTime = DateTime.UtcNow;
        _fired = false;
    }

    /// <summary>重置状态（新会话/恢复时）</summary>
    public void Reset()
    {
        _fired = false;
        _lastAudioTime = DateTime.UtcNow;
    }

    private void Check()
    {
        if (!_running || _fired) return;

        if ((DateTime.UtcNow - _lastAudioTime).TotalMilliseconds > _timeoutMs)
        {
            _fired = true;
            OnTimeout?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
