using System;
using Avalonia;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>程序入口 — Avalonia 桌面应用</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 初始化日志门面（默认 ConsoleLogger，后续可替换）
        Log.SetImpl(new Core.ConsoleLogger());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
