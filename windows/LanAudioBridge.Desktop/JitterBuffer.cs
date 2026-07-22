using System;
using System.Collections.Generic;

namespace LanAudioBridge.Desktop;

/// <summary>
/// JitterBuffer — 音频帧抖动缓冲
///
/// ## 职责
/// 消除 UDP 网络抖动导致的音频卡顿：
/// 1. 按序列号排序缓存到达的 PCM 帧
/// 2. 预缓冲（PreFill）：启动时攒满 N 帧再开始输出，消除冷启动卡顿
/// 3. 匀速输出：由外部 20ms 定时器驱动 Pull，保证播放节奏稳定
/// 4. 溢出保护：缓冲满时丢弃最老帧
///
/// ## 线程安全
/// Push 由网络回调线程调用，Pull 由定时器线程调用，内部加锁。
/// </summary>
public sealed class JitterBuffer
{
    private readonly int _capacity;       // 最大缓冲帧数
    private readonly int _preFillCount;   // 预缓冲帧数（攒满后才开始输出）
    private readonly object _lock = new();

    // 按 seq 排序的帧队列
    private readonly SortedDictionary<ushort, float[]> _buffer = new();
    private ushort _nextPullSeq;          // 下一个期望输出的 seq
    private bool _hasNextSeq;
    private bool _prefilled;              // 是否已完成预缓冲

    /// <summary>缓冲下溢次数（Pull 时无帧可取）</summary>
    public int UnderrunCount { get; private set; }

    /// <summary>溢出丢帧次数（缓冲满时丢弃）</summary>
    public int OverflowCount { get; private set; }

    /// <summary>当前缓冲帧数</summary>
    public int Count { get { lock (_lock) return _buffer.Count; } }

    /// <summary>是否已完成预缓冲（开始正常输出）</summary>
    public bool IsPrefilled => _prefilled;

    /// <param name="capacity">最大缓冲帧数（默认 5，即 100ms）</param>
    /// <param name="preFillCount">预缓冲帧数（默认 3，即 60ms）</param>
    public JitterBuffer(int capacity = 5, int preFillCount = 3)
    {
        _capacity = capacity;
        _preFillCount = preFillCount;
    }

    /// <summary>
    /// 推入一帧解码后的 PCM（由网络回调线程调用）
    /// </summary>
    /// <param name="seq">帧序列号</param>
    /// <param name="pcm">解码后的 float32 PCM 数据</param>
    public void Push(ushort seq, float[] pcm)
    {
        lock (_lock)
        {
            // 检测新会话：seq 大幅回退（>100 帧 = 2s）表示 Android 重新推流
            if (_hasNextSeq && SeqBefore(seq, _nextPullSeq))
            {
                int backward = (ushort)(_nextPullSeq - seq);
                if (backward > 100)
                {
                    // 新会话：重置缓冲，接受新帧
                    _buffer.Clear();
                    _prefilled = false;
                    _hasNextSeq = false;
                }
                else
                {
                    return; // 真正的迟到包，丢弃
                }
            }

            // 丢弃重复帧
            if (_buffer.ContainsKey(seq))
                return;

            // 溢出保护：缓冲满时丢弃最老帧
            if (_buffer.Count >= _capacity)
            {
                var oldest = _buffer.Keys.GetEnumerator();
                if (oldest.MoveNext())
                {
                    _buffer.Remove(oldest.Current);
                    OverflowCount++;
                }
            }

            _buffer[seq] = pcm;

            // 检查预缓冲是否完成
            if (!_prefilled && _buffer.Count >= _preFillCount)
            {
                _prefilled = true;
                // 设定起始输出 seq 为缓冲中最小的 seq
                var first = _buffer.Keys.GetEnumerator();
                if (first.MoveNext())
                {
                    _nextPullSeq = first.Current;
                    _hasNextSeq = true;
                }
            }
        }
    }

    /// <summary>
    /// 取出一帧 PCM（由 20ms 定时器线程调用）
    /// 返回 null 表示缓冲空（underrun），播放端应输出静音
    /// </summary>
    public float[]? Pull()
    {
        lock (_lock)
        {
            if (!_prefilled) return null;

            if (_buffer.TryGetValue(_nextPullSeq, out var pcm))
            {
                _buffer.Remove(_nextPullSeq);
                _nextPullSeq++;
                return pcm;
            }

            // 期望的 seq 不在缓冲中（丢包或乱序）
            // 如果缓冲中有更后面的帧，跳过空洞
            if (_buffer.Count > 0)
            {
                var first = _buffer.Keys.GetEnumerator();
                first.MoveNext();
                ushort oldest = first.Current;

                // 如果空洞超过 capacity/2，说明 seq 跳变（可能重连），重置
                int gap = (ushort)(oldest - _nextPullSeq);
                if (gap > _capacity)
                {
                    _nextPullSeq = oldest;
                    if (_buffer.TryGetValue(_nextPullSeq, out var p))
                    {
                        _buffer.Remove(_nextPullSeq);
                        _nextPullSeq++;
                        return p;
                    }
                }
            }

            // Underrun：无帧可取
            UnderrunCount++;
            _nextPullSeq++; // 跳过这个 seq，下次尝试下一个
            return null;
        }
    }

    /// <summary>重置缓冲（停止/重连时调用）</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _prefilled = false;
            _hasNextSeq = false;
            _nextPullSeq = 0;
            UnderrunCount = 0;
            OverflowCount = 0;
        }
    }

    /// <summary>判断 a 是否在 b 之前（处理 ushort 回绕）</summary>
    private static bool SeqBefore(ushort a, ushort b)
    {
        return (short)(a - b) < 0;
    }
}
