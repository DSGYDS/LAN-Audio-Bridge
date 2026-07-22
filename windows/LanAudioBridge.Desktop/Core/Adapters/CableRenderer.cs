using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using LanAudioBridge.Core.Infrastructure;
using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// CableRenderer — VB-CABLE 虚拟麦克风渲染适配器
///
/// 包裹 WasapiOut + BufferedWaveProvider，实现 IAudioRenderer。
/// 输出目标：CABLE Input（VB-Audio Virtual Cable）端点。
/// 用户需在目标应用中选择 CABLE Output 作为麦克风。
/// </summary>
public sealed class CableRenderer : IAudioRenderer, IDisposable
{
    private const string Tag = "CableRenderer";

    private IWavePlayer? _output;
    private BufferedWaveProvider? _buffer;
    private float _volume = 1.0f;
    private bool _muted;
    private bool _disposed;

    public bool IsReady => _output != null;

    public bool Prepare(AudioConfig config)
    {
        if (_output != null) return true;

        var cableDevice = FindCableDevice();
        if (cableDevice == null)
        {
            Log.E(Tag, "CABLE Input device not found");
            return false;
        }

        try
        {
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(config.SampleRate, 1);
            _buffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(config.BufferMs),
                DiscardOnBufferOverflow = true,
            };

            _output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, config.WasapiLatency);
            _output.Init(_buffer);
            return true;
        }
        catch (Exception ex)
        {
            Log.E(Tag, $"Prepare failed: {ex.Message}");
            Release();
            return false;
        }
    }

    public void Play()
    {
        _output?.Play();
    }

    public void Stop()
    {
        _output?.Stop();
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_output != null)
            _output.Volume = _muted ? 0f : _volume;
    }

    public void Mute(bool muted)
    {
        _muted = muted;
        if (_output != null)
            _output.Volume = muted ? 0f : _volume;
    }

    public void FeedPcm(byte[] data, int offset, int count)
    {
        _buffer?.AddSamples(data, offset, count);
    }

    public void Release()
    {
        if (_disposed) return;
        _disposed = true;

        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose() => Release();

    // ── 设备查找 ──

    /// <summary>
    /// 查找 CABLE Input 设备。优先精确匹配，回退模糊匹配。
    /// </summary>
    private static MMDevice? FindCableDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var dev in devices)
        {
            if (dev.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        foreach (var dev in devices)
        {
            var name = dev.FriendlyName;
            if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        return null;
    }
}
