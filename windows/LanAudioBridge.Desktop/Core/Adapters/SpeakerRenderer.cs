using System;
using NAudio.Wave;
using LanAudioBridge.Core.Infrastructure;
using LanAudioBridge.Desktop;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// SpeakerRenderer — 扬声器渲染适配器
///
/// 包裹 WaveOutEvent + BufferedWaveProvider，实现 IAudioRenderer。
/// 输出目标：系统默认扬声器设备。
/// </summary>
public sealed class SpeakerRenderer : IAudioRenderer, IDisposable
{
    private const string Tag = "SpeakerRenderer";

    private IWavePlayer? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _waveFormat;
    private float _volume = 1.0f;
    private bool _muted;
    private bool _disposed;

    public bool IsReady => _output != null;

    public bool Prepare(AudioConfig config)
    {
        if (_output != null) return true;

        try
        {
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(config.SampleRate, 1);
            _buffer = new BufferedWaveProvider(_waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(config.BufferMs),
                DiscardOnBufferOverflow = true,
            };

            _output = new WaveOutEvent { DesiredLatency = config.WaveOutLatency };
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
}
