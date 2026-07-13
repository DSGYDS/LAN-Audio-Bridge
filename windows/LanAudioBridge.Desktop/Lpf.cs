using System;

namespace LanAudioBridge.Desktop;

/// <summary>
/// 二阶 Butterworth 低通滤波器，截至频率 7kHz
///
/// ⚠️ 当前未使用（ProcessInPlace 调用已注释）
/// 改用 Android 端 200Hz HPF 方案解决底噪，保留此文件供未来参考
/// 决策记录：2026-07-09 — 7kHz LPF 会切掉高频细节，HPF 方案更优
/// </summary>
internal sealed class Lpf
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    /// <summary>以给定采样率构造 7kHz Butterworth LPF</summary>
    public Lpf(int sampleRate = 48000)
    {
        double w0 = 2.0 * Math.PI * 7000.0 / sampleRate;
        double q = 1.0 / Math.Sqrt(2.0);
        double alpha = Math.Sin(w0) / (2.0 * q);
        double a0Denom = 1.0 + alpha;
        _b0 = (float)(((1.0 - Math.Cos(w0)) / 2.0) / a0Denom);
        _b1 = _b0;
        _b2 = _b0;
        _a1 = (float)((-2.0 * Math.Cos(w0)) / a0Denom);
        _a2 = (float)((1.0 - alpha) / a0Denom);
    }

    /// <summary>就地滤波，返回相同数组引用</summary>
    public float[] ProcessInPlace(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            samples[i] = y;
        }
        return samples;
    }

    /// <summary>滤波并返回新数组（不修改输入）</summary>
    public float[] Process(float[] samples)
    {
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            result[i] = y;
        }
        return result;
    }

    /// <summary>重置滤波器状态</summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0f;
    }
}
