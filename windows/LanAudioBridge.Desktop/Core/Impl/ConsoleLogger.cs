using System;
using System.IO;

namespace LanAudioBridge.Core;

/// <summary>
/// ConsoleLogger — ILogger 控制台 + 文件实现
/// DEBUG=灰色 / INFO=白色 / WARN=黄色 / ERROR=红色(stderr)
/// 同时写入日志文件（WinExe 无控制台时仍可查看）
/// </summary>
public class ConsoleLogger : ILogger
{
    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "debug.log");
    private static readonly object _fileLock = new();

    public void Debug(string tag, string msg) => Write("DBG", tag, msg, ConsoleColor.Gray);
    public void Info(string tag, string msg) => Write("INF", tag, msg, ConsoleColor.White);
    public void Warn(string tag, string msg) => Write("WRN", tag, msg, ConsoleColor.Yellow);
    public void Error(string tag, string msg) => WriteErr("ERR", tag, msg);
    public void Error(string tag, string msg, Exception ex) => WriteErr("ERR", tag, $"{msg}: {ex}");

    private static void Write(string level, string tag, string msg, ConsoleColor color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{level}][{tag}] {msg}";
        try
        {
            var orig = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = orig;
        }
        catch { /* WinExe 无控制台时忽略 */ }
        WriteToFile(line);
    }

    private static void WriteErr(string level, string tag, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{level}][{tag}] {msg}";
        try { Console.Error.WriteLine(line); } catch { }
        WriteToFile(line);
    }

    private static void WriteToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { /* 忽略文件写入失败 */ }
    }
}
