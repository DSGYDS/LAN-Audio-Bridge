# LAN Audio Bridge — 断线重连方案 v2

> 设计定稿：2026-07-12
> 状态：📐 已设计，待编码实现

---

## 一、设计原则

1. **WiFi 状态 ≠ 连接状态** — WiFi 连着但链路断了的情况一定存在，不能只靠 NetworkCallback
2. **Windows 参与状态感知，不参与重连** — Windows 负责发现"音频断了"并更新状态，但重连动作永远由 Android 发起
3. **优先恢复，再扫描** — 先试上一次成功的 IP，失败再 mDNS
4. **复用已有流程** — 重连逻辑应调用已有的 discover() → handshake() → startStreaming()，不重新写一套推流启动
5. **旁路观测** — ReconnectionManager 不侵入 AudioPipeline，不接管 UI

---

## 二、架构关系

```
┌────────────────┐
│ NetworkMonitor │  ← 监听 WiFi 状态变化
└────────┬───────┘
         │ onLost() / onAvailable()
         ▼
┌────────────────────┐     ┌───────────────────────┐
│ ConnectionState    │ ←── │ ReconnectionManager   │
│ Manager            │     │                       │
│                    │     │ tryRecover() {        │
│ DISCONNECTED       │     │   1. 试最后 IP → HELLO│
│ SEARCHING          │     │   2. 不行就 mDNS      │
│ FOUND              │     │   3. HELLO            │
│ CONNECTING         │     │   4. startStreaming() │
│ CONNECTED          │     │   5. retry × 5        │
│ STREAMING ◄─────── │ ── │ }                     │
│ RECOVERING ◄────── │     └───────────────────────┘
│ FAILED             │
└────────────────────┘
         ▲
         │ 连续 3 秒没收 Audio
┌────────────────┐
│ Audio Watchdog │  ← Windows 端，只更新状态，不发起重连
└────────────────┘
```

---

## 三、状态转换规则（强制约束）

### 合法转换图

```
STREAMING ──(连接异常)──→ RECOVERING
RECOVERING ──(HELLO成功 + 第一帧Audio)──→ STREAMING
RECOVERING ──(重试5次失败)──→ FAILED
FAILED ──(用户手动重新连接)──→ SEARCHING
SEARCHING ──(发现设备)──→ FOUND
FOUND ──(握手发起)──→ CONNECTING
CONNECTING ──(HELLO_ACK)──→ CONNECTED
CONNECTED ──(第一帧Audio)──→ STREAMING
```

### 禁止的转换

| 禁止路径 | 原因 |
|----------|------|
| ❌ **RECOVERING → CONNECTED** | 握手成功不等同音频恢复，必须收到第一帧 Audio 才能回到 STREAMING |
| ❌ **RECOVERING → SEARCHING** | RECOVERING 内部包含重试循环，不应跳回 SEARCHING |
| ❌ **FAILED → RECOVERING** | 失败后必须用户手动触发，不允许自动循环 |
| ❌ **STREAMING → 直接其他链路** | 必须先经过 RECOVERING 再决定降级 |

### 恢复不是"握手成功"

RECOVERING 退出到 STREAMING **必须同时满足两个条件**：
1. HELLO → HELLO_ACK 握手成功
2. 收到第一帧 Audio 包并解码成功

只满足条件 1 停留在 CONNECTED，不视为恢复。

---

## 四、五路 RECOVERING 触发源

进入 RECOVERING 不代表一定会走重连循环。它只是状态机的一个状态，谁来触发都可以：

| # | 触发源 | 平台 | 说明 |
|---|--------|------|------|
| 1 | **WiFi 断连** | Android | `ConnectivityManager.NetworkCallback.onLost()` — 最直接的触发 |
| 2 | **HELLO 失败** | Android | `doHandshake()` 返回 false，说明对端不可达 |
| 3 | **Socket 异常** | Android | `UdpSender` 或握手 socket 抛出异常 |
| 4 | **音频发送异常** | Android | `AudioPipeline` 持续编码/发送失败 |
| 5 | **用户手动重连** | Android | UI 上点"重试"按钮 |

所有触发源统一调用 `stateManager.update(RECOVERING)`，然后由 `ReconnectionManager` 接管。

---

## 五、Android 端 — ReconnectionManager

### 职责

- 注册 NetworkCallback
- 监听 ConnectionStateManager 的状态变化
- 进入 RECOVERING 后自动启动重连循环
- 重连循环：优先最后 IP → mDNS → HELLO → startStreaming → retry × 5

### ⚠️ stopStreaming() 的边界约束

进入 RECOVERING 后调用 `pipe.stopStreaming()`，**必须明确释放什么、保留什么：**

```
释放（可以销毁重建）：
├── Audio capture （MicrophoneCapturer / SystemAudioCapturer）
├── Encoder （AudioEncoder）
└── UDP sender socket

保留（绝对不能清空）：
├── lastKnownHost           ← 最后成功连接的主机 IP
├── routeMode               ← 最后选择的路由模式
├── ConnectionStateManager  ← 状态机对象
├── ReconnectionManager     ← 重连管理器自身
└── AudioPipeline 实例       ← 可重复 start/stop
```

**错误示例（AI 禁止这样写）：**
```kotlin
fun stopStreaming() {
    lastKnownHost = null   // ❌ 清空后重连不知道连谁
    closeSocket()          // ❌ 不应在此关闭 socket 资源
    stateManager = null    // ❌ 状态机不应该被销毁
    pipeline = null        // ❌ 管线实例保留，可重复 start
}
```

**正确做法：**
```kotlin
fun stopStreaming() {
    capturer?.stop()
    encoder?.stop()
    sender?.stop()         // 停止发送，不销毁 sender 对象
    isStreaming = false
    // 不清除 lastKnownHost
    // 不清除 stateManager
    // 不销毁 pipeline
}
```

恢复时直接用保留的 `lastKnownHost` 和已有 pipeline 对象重新 start。

### 重连循环详细流程

```
STREAMING
    ↓  触发源（五路之一）
RECOVERING
    │
    ├── 停止当前推流（pipe.stopStreaming()）
    │    释放 capture / encoder / sender，保留 lastKnownHost / stateManager / pipeline
    │
    ├── 尝试 1: 直接 HELLO 最后一次成功 IP
    │    ├── HELLO_ACK 收到 → startStreaming() → 成功 → STREAMING ✅
    │    └── 失败 → 等 2 秒
    │
    ├── 尝试 2: mDNS 扫描（上限 3 秒）
    │    ├── 发现设备 → HELLO → startStreaming() → 成功 → STREAMING ✅
    │    └── 失败 → 等 2 秒
    │
    ├── 尝试 3 ~ 5: 重复 mDNS + HELLO + start
    │
    └── 5 次全失败 → FAILED ❌（UI 显示"重连失败，请手动重试"）
```

**关键约束：**
- ReconnectionManager **不知道 AudioPipeline 内部细节**，只调用已有公开方法
- ReconnectionManager **不知道 UI**，只通过回调通知状态变化
- **每次重试都是完整流程**：discover() → handshake() → startStreaming()，复用用户点击"开始"的同一代码路径

### 接口定义

```kotlin
class ReconnectionManager(
    private val context: Context,
    private val stateManager: ConnectionStateManager,
    private val pipeline: AudioPipeline,
    private val onRecover: suspend (host: String, mode: Int) -> Boolean
) {
    // onRecover 由 MainActivity 注入
    // 内部就是现有那段 discover → handshake → start 的代码
}
```

`onRecover` 的定义：

```kotlin
// MainActivity 提供，复用已有逻辑
onRecover = { host, mode ->
    val handshakeOk = doHandshake(host, route)
    if (!handshakeOk) return@onRecover false
    val capMode = routeToCapture(route)
    pipe.startStreaming(capMode, proj, act, host)
}
```

---

## 六、Windows 端 — Audio Watchdog

### 职责

**只更新状态，不发起重连。**

```
STREAMING
    ↓  连续 3 秒没收到 AUDIO 包
RECOVERING
    ↓  收到 HELLO（Android 发起重连）
CONNECTED
    ↓  收到第一帧 Audio
STREAMING
```

### 实现

```csharp
// 配置常量，放 AudioEngine 类顶部或单独 AudioConfig.cs
// 建议值 3000ms，可按需调（1000 / 5000 / 10000）
private const int AudioTimeoutMs = 3000;

private DateTime _lastAudioTime = DateTime.MinValue;

// 在 DecodeAndProcess 成功后更新
_lastAudioTime = DateTime.UtcNow;

// 单独 watchdog 线程（Tick 循环）
while (_running) {
    if (_state == ConnectionState.Streaming &&
        (DateTime.UtcNow - _lastAudioTime).TotalMilliseconds > AudioTimeoutMs)
    {
        _stateManager.Update(ConnectionState.Recovering);
        // 不发起重连，等 HELLO
    }
    Thread.Sleep(500); // 每 500ms 检查一次
}
```

**状态恢复（自动，无需额外代码）：**
- Android 发来 HELLO → `HandshakeServer` 处理 → `OnHelloReceived` 触发
- `ConnectionStateManager` 在 `OnHelloReceived` 中设为 `Connected`
- `AudioEngine` 解码第一帧 → `OnFirstFrameDecoded` → 设为 `Streaming`

### 当前 ConnectionStateManager.cs 已有的状态流转

```csharp
// 已在 ConnectionStateManager.cs 定义的流转
OnHelloReceived  → Connected  （已有）
OnFirstFrameDecoded → Streaming（已有）
Watchdog 超时     → Recovering（新增）
```

---

## 七、文件改动清单

### Android 端

| 文件 | 操作 | 说明 |
|------|------|------|
| `net/ReconnectionManager.kt` | **新建** | WiFi 回调 + 重连循环，复用已有流程 |
| `MainActivity.kt` | 修改 | 注入 `onRecover` 回调，粘合 ReconnectionManager |
| `ConnectionStateManager.kt` | 不动或微调 | 已有 RECOVERING 状态，确认入口 |

### Windows 端

| 文件 | 操作 | 说明 |
|------|------|------|
| `AudioEngine.cs` | 修改 | watchodg 计时 + 3 秒超时检测 |
| `ConnectionStateManager.cs` | 修改 | 暴露 `Update(Recovering)` 方法，联动 HandshakeServer + AudioEngine 自动恢复 |

---

## 八、不做的事（第一期）

| 不做 | 原因 |
|------|------|
| 心跳包 | 双向心跳需要两端新协议 + 线程，且 WiFi 回调 + 3 秒 watchodg 已覆盖大多数场景 |
| 链路降级（热点/蓝牙/USB） | 单独功能，后续在链路列表里插项即可 |
| Windows 发起重连 | 违背设计原则，重连永远是 Android 主动 |
| mDNS 每次都扫 | 先试最后 IP，失败才扫 |

---

## 九、验证清单

| # | 验证项 | 方法 |
|---|--------|------|
| 1 | WiFi 断开后 Android 进入 RECOVERING | 拔网线/关 WiFi |
| 2 | WiFi 恢复后自动重连成功 | 开 WiFi，观察重连循环 |
| 3 | 重连时优先试最后 IP（最快路径） | 日志确认 |
| 4 | 重连流程复用现有 discover/handshake/start | 代码审查 |
| 5 | 5 次重试全失败后进入 FAILED | 关闭 Windows 端再试 |
| 6 | Windows 3 秒没收 Audio 进入 RECOVERING | 暂停 Android 端 |
| 7 | Android 发 HELLO 后 Windows 自动恢复 STREAMING | 恢复 Android 端 |
