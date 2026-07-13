using System;

namespace LanAudioBridge.Core.Infrastructure;

/// <summary>
/// Log — ILogger 静态门面
/// 全局唯一日志入口，所有模块通过此类输出日志。
/// 启动时调用 Log.SetImpl() 注入具体实现。
/// </summary>
public static class Log
{
    private static ILogger _impl = new ConsoleLogger();

    public static void SetImpl(ILogger impl) => _impl = impl ?? throw new ArgumentNullException(nameof(impl));

    public static void D(string tag, string msg) => _impl.Debug(tag, msg);
    public static void I(string tag, string msg) => _impl.Info(tag, msg);
    public static void W(string tag, string msg) => _impl.Warn(tag, msg);
    public static void E(string tag, string msg) => _impl.Error(tag, msg);
    public static void E(string tag, string msg, Exception ex) => _impl.Error(tag, msg, ex);
}
