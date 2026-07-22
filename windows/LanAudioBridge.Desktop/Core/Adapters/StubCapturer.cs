using System;
using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// StubCapturer — 采集器桩实现（Windows 端不采集音频）
///
/// Windows 是纯接收端，不需要采集功能。
/// 所有方法抛出 NotSupportedException。
/// 以后 iOS/macOS 加入时各自实现真正的采集器。
/// </summary>
public sealed class StubCapturer : IAudioCapturer
{
    public CapturerType SourceType => CapturerType.Microphone;

    public bool Prepare(AudioConfig config)
        => throw new NotSupportedException("Windows 端不支持音频采集");

    public bool Start()
        => throw new NotSupportedException("Windows 端不支持音频采集");

    public int ReadFrame(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Windows 端不支持音频采集");

    public void Stop() { }

    public void Release() { }
}
