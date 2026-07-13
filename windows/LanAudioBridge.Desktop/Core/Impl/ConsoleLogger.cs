using System;

namespace LanAudioBridge.Core;

/// <summary>
/// ConsoleLogger — ILogger 控制台实现
/// DEBUG=灰色 / INFO=白色 / WARN=黄色 / ERROR=红色(stderr)
/// </summary>
public class ConsoleLogger : ILogger
{
    public void Debug(string tag, string msg) => Write("DBG", tag, msg, ConsoleColor.Gray);
    public void Info(string tag, string msg) => Write("INF", tag, msg, ConsoleColor.White);
    public void Warn(string tag, string msg) => Write("WRN", tag, msg, ConsoleColor.Yellow);
    public void Error(string tag, string msg) => WriteErr("ERR", tag, msg);
    public void Error(string tag, string msg, Exception ex) => WriteErr("ERR", tag, $"{msg}: {ex}");

    private static void Write(string level, string tag, string msg, ConsoleColor color)
    {
        var orig = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}][{tag}] {msg}");
        Console.ForegroundColor = orig;
    }

    private static void WriteErr(string level, string tag, string msg)
    {
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}][{tag}] {msg}");
    }
}
