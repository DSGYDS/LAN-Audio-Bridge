using System;

namespace LanAudioBridge.Core;

/// <summary>
/// ILogger — 统一日志接口
/// </summary>
public interface ILogger
{
    void Debug(string tag, string msg);
    void Info(string tag, string msg);
    void Warn(string tag, string msg);
    void Error(string tag, string msg);
    void Error(string tag, string msg, Exception ex);
}
