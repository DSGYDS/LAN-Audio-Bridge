# LAN Audio Bridge — 延迟调优方案（修订版 v3）

> 定稿：2026-07-12
> 优先级：B 级
> 根据 黑面煞 的审查意见 v2 迭代

---

## 核心思路

**监控先行，逐步逼近，数据驱动。**

1. 先加 Buffer 水位监控（统计 + 异常日志）
2. 用数据而不是耳朵决定每一步的参数
3. 每个参数从保守值开始，逐步降低到稳定工作的最低值
4. 每一步隔离开，出问题快速定位

---

## 第一步（必须先做）：加入播放队列监控

### 方案

在 `AudioRouter` 中暴露缓冲水位只读属性，内部维护一个滑动统计器。

**`AudioRouter.cs` 新增：**

```csharp
// ── 缓冲水位监控（用于延迟调优调试） ──
public sealed class BufferStats
{
    public double CurrentMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double AvgMs { get; set; }
    public int SampleCount { get; set; }
}

private BufferStats _speakerStats = new();
private double _prevBufMs;
private int _monitorFrameCount;

private const int MonitorInterval = 50; // 每 50 帧（≈1s）更新一次统计

/// <summary>获取当前扬声器缓冲水位统计，用于调试和延迟监控</summary>
public BufferStats SpeakerBufferStats => _speakerStats;

/// <summary>每次处理一帧音频后调用，更新缓冲统计并触发异常日志</summary>
public void OnFrameProcessed()
{
    var buf = _speakerBuf;
    if (buf == null) return;

    _monitorFrameCount++;
    if (_monitorFrameCount % MonitorInterval != 0) return;

    var ms = buf.BufferedDuration.TotalMilliseconds;
    var bytes = buf.BufferedBytes;

    // 更新统计
    if (_speakerStats.SampleCount == 0)
    {
        _speakerStats.MinMs = ms;
        _speakerStats.MaxMs = ms;
    }
    else
    {
        if (ms < _speakerStats.MinMs) _speakerStats.MinMs = ms;
        if (ms > _speakerStats.MaxMs) _speakerStats.MaxMs = ms;
    }
    _speakerStats.CurrentMs = ms;
    _speakerStats.AvgMs = ((_speakerStats.AvgMs * _speakerStats.SampleCount) + ms) / (_speakerStats.SampleCount + 1);
    _speakerStats.SampleCount++;

    // 异常日志：只在状态变化超过阈值时打印
    var delta = Math.Abs(ms - _prevBufMs);
    var shouldLog = false;
    var reason = "";

    if (ms < 20)                         { shouldLog = true; reason = "UNDERRUN"; }
    else if (ms > BufferMs * 0.8)        { shouldLog = true; reason = "NEAR_FULL"; }
    else if (delta > 30 && _prevBufMs > 0) { shouldLog = true; reason = $"JUMP {delta:F0}ms"; }

    if (shouldLog)
    {
        Console.WriteLine(
            $"[BufMonitor] {reason} | " +
            $"Cur={ms:F0}ms Min={_speakerStats.MinMs:F0}ms " +
            $"Max={_speakerStats.MaxMs:F0}ms Avg={_speakerStats.AvgMs:F0}ms " +
            $"Bytes={bytes}"
        );
    }

    _prevBufMs = ms;
}
```

**`AudioEngine.cs` 帧循环末尾调用：**

```csharp
_router.OnFrameProcessed();
```

### 效果

不刷屏。平时静默，只有以下情况打印：

- **UNDERRUN**：Buffer < 20ms，快饿死了，即将爆音
- **NEAR_FULL**：Buffer > BufferMs × 80%，快堆满了，消费跟不上
- **JUMP**：水位突变超过 30ms，可能网络抖动

跑 5 分钟后看一眼 Min/Max/Avg，秒懂管道的健康状况。

---

## 第二步：逐步降低 BufferMs

### 方法

不预设最终值。从 300 开始，每次砍半或降一档，每档稳定运行后再降。

```
300ms（当前）
  ↓  稳定后
150ms
  ↓  稳定后
100ms
  ↓  稳定后
 80ms（目标下限）
```

**`AudioRouter.cs` 第 35 行：**

```
private const int BufferMs = 300;
```

每次调这一个数字即可。

### 判断标准

每档跑 5 分钟连续推流 + 热切切换：

- **UNDERRUN 日志出现 0 次** ✅ → 继续降
- **UNDERRUN 偶尔出现** ⚠️ → 回退到上一档
- **BUFFER_NEAR_FULL 频繁出现** ⚠️ → 说明消费慢，不降反升

### 预期

目标值 80~150ms，具体取决于你设备和网络的实际表现。

---

## 第三步：WaveOutEvent DesiredLatency 80 → 50

### 改动

**`AudioRouter.cs` 第 285 行：**

```
_speakerOut = new WaveOutEvent { DesiredLatency = 80 };
→
_speakerOut = new WaveOutEvent { DesiredLatency = 50 };
```

### 风险

机器差异大。50 是保守起步值，出问题回退 80。

---

## 第四步：WasapiOut（CABLE Input）DesiredLatency 80 → 50

### 改动

**`AudioRouter.cs` 第 318 行：**

```
_micOut = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, 80);
→
_micOut = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, 50);
```

---

## 第五步：Android AudioRecord 缓冲（暂不动）

### 说明

Android 的 `bufferSize` 不影响稳态延迟，只影响抗抖动能力。**今年不动这两处。**

保留在计划中仅作记录：

| 位置 | 当前值 | 调优方向 | 触发条件 |
|------|--------|---------|---------|
| `MicrophoneCapturer.kt` | `*4` | `*2` | 阶段四稳定且 Mic 从未出 underrun |
| `SystemAudioCapturer.kt` | `*32` | `*16` | 阶段四稳定且系统音频从未出 underrun |

---

## 后续：参数配置化

这几个值迟早要进 GUI，提前留好接口。后续改动：

```csharp
public sealed class AudioConfig
{
    public int BufferMs { get; set; } = 80;
    public int WaveOutLatency { get; set; } = 50;
    public int WasapiLatency { get; set; } = 50;
    // ...
}
```

构造函数改为接收配置对象：

```csharp
public AudioRouter(AudioConfig config)
{
    BufferMs = config.BufferMs;
    // ...
}
```

**本期不做，留作计划。**

---

## 实施顺序总结

```
第一步  加入 Buffer 水位监控（统计 + 异常日志）
  ↓
第二步  逐步降低 BufferMs：300 → 150 → 100 → 80
  ↓
第三步  WaveOutEvent DesiredLatency 80 → 50
  ↓
第四步  WasapiOut DesiredLatency 80 → 50
  ↓
第五步  Android buffer 评估（大概率不动）
  ↓
后续    参数集中管理 + GUI 配置项
```

| 阶段 | 改动量 | 风险 | 决策依据 |
|------|--------|------|---------|
| 一 | 新增约 50 行监控代码 | 无 | 先做 |
| 二 | 改 1 个数字，逐档测试 | 中 | BufferStats 监控数据 |
| 三 | 改 1 个数字 | 中 | 机器实测 |
| 四 | 改 1 个数字 | 中 | 机器实测 |
| 五 | 不改 | 无 | 除非数据支撑 |

---

## 回滚

每档测试前手动备份。最终回滚点：

```
D:\backup\LAN-Audio-Bridge-bak_20260712_1810
```
