# MEMORY.md — LAN Audio Bridge 项目记忆

> 最后更新：2026-07-13 02:30
> 全量重构（第二轮）完成 + 双端编译验证通过 + 备份已创建
> 当前有效备份：`D:\backup\LAN-Audio-Bridge-bak_20260713_0215`

---

## 项目概况

**项目：** LAN Audio Bridge — 局域网实时音频桥
**目标：** 手机音频（系统音频/麦克风/混音）零配置低延迟传到电脑播放，支持四条管线
**架构：** Android（Kotlin/Jetpack Compose）→ UDP Opus（48kHz/16bit/单声道/128kbps）→ Windows（C#/Avalonia/NAudio/VB-CABLE）

**当前阶段：** Phase 1-6 核心功能已完成，Phase 7-10 待完善

---

## 文件结构

```
D:\LAN-Audio-Bridge\
├── android/app/src/main/java/com/lanbridge/app/
│   ├── MainActivity.kt              ← UI + 握手/热切 + 快速测试
│   ├── ConnectionStateManager.kt    ← 连接状态机（旁路观测层）
│   ├── StreamingService.kt          ← 前台保活 Service
│   ├── MediaProjectionService.kt    ← 系统音频授权代理
│   ├── audio/
│   │   ├── AudioPipeline.kt         ← 管线调度（采集→编码→UDP）
│   │   ├── MicrophoneCapturer.kt    ← AudioRecord 麦克风采集 + 200Hz HPF
│   │   ├── SystemAudioCapturer.kt   ← MediaProjection 系统音频采集
│   │   ├── AudioEncoder.kt          ← Concentus Opus 编码（128k/FEC/CVBR）
│   │   └── AudioResampler.kt        ← ❌ 已删除（26-07-12清理）
│   ├── net/
│   │   ├── UdpSender.kt             ← UDP Opus 发送 + PacketHeader 14B
│   │   ├── LanAudioDiscovery.kt     ← NsdManager mDNS 发现（串行化）
│   │   ├── ReconnectionManager.kt   ← 断线重连（WiFi 监听 + 5 次重试）
│   │   ├── PacketHeader.kt          ← 14B 协议头编解码（Magic/Version/Type/Seq/PayloadLen）
│   │   └── ProtocolTypes.kt         ← PacketType 枚举（HELLO/ROUTE/AUDIO等）
│   └── ui/theme/Theme.kt            ← Material3 配色（动态取色+深色模式）
│
├── windows/LanAudioBridge.Desktop/
│   ├── Program.cs                   ← 启动入口
│   ├── App.axaml / App.axaml.cs     ← Avalonia 应用入口
│   ├── MainWindow.axaml / .cs       ← UI + 生命周期 + Watchdog 事件
│   ├── ConnectionStateManager.cs    ← 连接状态机（旁路观测层）
│   ├── AudioEngine.cs               ← UDP 接收 → Opus 解码 → Router + Watchdog
│   ├── AudioRouter.cs               ← 四模式分发（Speaker/Mic/Both/MicOnlySys）
│   ├── HandshakeServer.cs           ← HELLO/ROUTE 二进制协议（PacketHeader）
│   ├── PacketHeader.cs              ← 14B 协议头编解码（与 Android 一致）
│   ├── ProtocolTypes.cs             ← PacketType 枚举（与 Android 一致）
│   ├── MdnsPublisher.cs             ← Makaretu.Dns mDNS 发布
│   └── Lpf.cs                       ← 7kHz LPF（已停用，保留备查）
│
├── windows/scripts/
│   ├── install_driver.bat           ← VB-CABLE 安装
│   └── uninstall_driver.bat         ← VB-CABLE 卸载
│
├── poc/               🧪 WiFi Direct PoC（控制台版，保留）
├── poc-ui/            🧪 WiFi Direct PoC（WPF UI 版，保留）
├── proto/              📜 协议定义（空，待实施）
│   ├── PROTOCOL.md                  ← 14B PacketHeader 协议定义
│   ├── RECONNECT.md                 ← 断线重连设计文档
│   ├── LINK_DOWNGRADE.md            ← 四级链路降级架构
│   ├── LATENCY_TUNING.md            ← 延迟调优方案（v3 修订版）
│   └── WIFI_DIRECT.md               ← WiFi Direct 设计文档（v3）
├── WIFI Direct开发方向.md          ← 最新开发方向（正统 P2P 架构）
├── archive/libs/VB-Cable/           ← VB-CABLE 驱动包
├── archive/win_backup_20260703_152103/ ← Win 端旧备份
│
├── memory/                          ← 每日笔记
│   ├── 2026-07-04.md
│   └── 2026-07-12.md
│
├── MEMORY.md                        ← 本文件（项目记忆）
├── 项目进度存档.md                   ← 逐阶段完成状态
├── 项目总览.md                       ← 架构/技术决策总览
└── 局域网音频桥项目计划书.md          ← 完整项目计划书
```

---

## 四模式管线

| 模式 | 简称 | Android 采集 | Windows 路由 | 状态 |
|------|------|-------------|-------------|------|
| 模式1 | Speaker | SystemAudioCapturer | SpeakerOnly（扬声器） | ✅ |
| 模式2 | Monitor | Mix（SystemAudio + Microphone） | SpeakerOnly（扬声器） | ✅ |
| 模式3 | Virtual Mic | MicrophoneCapturer | MicOnly（CABLE Input） | ✅ |
| 模式4 | Shenanigans | SystemAudioCapturer | MicOnlySys（CABLE Input） | ✅ |

---

## 关键技术决策

### 音频处理链
- **Android→Win 链路**：AudioRecord/MediaProjection → Concentus Opus → UDP 12345 → Concentus Opus 解码 → NAudio 播放
- **Opus 参数**：128kbps（原64k）、complexity 10、FEC启用、CVBR、PacketLoss 15%
- **Windows 7kHz LPF**：已关闭（2026-07-09），去低频底噪改为 Android 端 200Hz HPF
- **SpeakerGain**：1.8→1.0（2026-07-09，消除削波失真）
- **WaveOutEvent 保留**：不用 WasapiOut（2026-07-09），因其共享模式 Dispose 异步导致热切卡死
- **麦克风音量缩放**：之前未在 readFrame 应用 volume，2026-07-12 修复 MicrophoneCapturer 补上 applyVolume()

### 网络和协议
- **音频端口**：UDP 12345，包格式 `[4B seq大端][4B pcmSize大端][Opus载荷]`
- **控制端口**：UDP 12347，文本协议 `HELLO:n` / `ROUTE:n`
- **mDNS 发现**：Android NsdManager（串行化 resolve 解决 IllegalStateException），Windows Makaretu.Dns
- **协议头抽象尝试**：2026-07-12 实现 12B 包头后回滚，优先级低于状态机和断线重连

### 连接状态机
- **ConnectionStateManager**：2026-07-12 实现，双端独立旁路观测层
- 状态：Disconnected→Searching→Found→Connecting→Connected→Streaming/Recovering/Failed
- 不替换 streaming/_running，作为 UI 观测层共存

### 断线重连（2026-07-12 实现）
- **设计文档**：`proto/RECONNECT.md`（状态转换约束、stopStreaming 边界、Watchdog 配置化）
- **五路触发源**：WiFi 断连、HELLO 失败、Socket 异常、音频发送异常、用户手动
- **重连策略**：5 次重试，优先直接 HELLO 最后 IP → 失败后 mDNS 扫描
- **Windows Watchdog**：3 秒无音频 → RECOVERING（只感知状态，不发起重连）
- **stopStreaming 边界**：释放 capture/encoder/sender，保留 lastKnownHost/stateManager/pipeline
- **状态转换约束**：RECOVERING → STREAMING 必须同时满足 HELLO 成功 + 第一帧 Audio

### 虚拟麦克风
- **方案 B**：不切换系统默认麦克风，直接 WasapiOut 写 CABLE Input
- VB-CABLE 环回缓冲区原理：CABLE Input ← 写入 → CABLE Output ← 应用读取
- **MicSwitcher.cs 已删除**（IPolicyConfig 未公开 API 不稳定）

### 已删除/移除
- `MicSwitcher.cs`：COM IPolicyConfig 方案废弃
- `AudioResampler.kt`：48kHz 固定无需重采样
- `AudioEngine.cs` 中的 `FloatPool`、`_lpf` 字段：未使用

---

## 性能参数

| 项目 | 当前值 | 备注 |
|------|--------|------|
| AudioRecord 缓冲 | FRAME_BYTES * 4 = 7680 bytes (~80ms) | |
| MediaProjection 缓冲 | FRAME_BYTES * 32 = 61440 bytes (~640ms) | 吸收系统音频抖动 |
| Win BufferMs | **100ms**（已从 300 逐档下调，稳定工作最低值） | ✅ 调优完成 |
| Win DesiredLatency | **100ms**（WaveOutEvent，80→50卡顿→100稳定） | ✅ 调优完成 |
| Win Mic latency | **50ms**（WasapiOut CABLE Input，80→50稳定） | ✅ 调优完成 |
| 淡入 | 50ms × 48000 = 2400 采样点 | 防切模式爆音 |
| 音频帧 | 20ms（960 采样点 @ 48kHz × 2字节 = 1920 字节） | |

---

## 待办优先级

| 优先级 | 改动 | 文件/位置 | 预估 |
|--------|------|----------|------|
| **S** | ✅ 断线重连（已完成 2026-07-12） | ReconnectionManager + Watchdog | ~140行 |
| **S** | ✅ 协议头抽象（已完成 2026-07-12） | proto + 双端 PacketHeader | ~80行 |
| **S** | ✅ 延迟调优（已完成 2026-07-12） | BufferMs 300→100, WaveOut 80→100, Wasapi 80→50 | ~50行监控+3处数值 |
| **S** | ✅ 全量重构（第二轮，2026-07-13） | Windows 9 文件 + Android 8 文件，提取方法、添加异常处理、改进结构 | ~300行 |
| **S** | **四级链路降级**（架构确认） | 见 proto/LINK_DOWNGRADE.md | 见各阶段 |
| **A** | WiFi Direct P2P 建链（Android 端） | WifiP2pManager + UI 引导 | ~200行 |
| **A** | 降级/升回 LinkProbe 逻辑 | ReconnectionManager 扩展 | ~100行 |
| **B** | 参数集中管理 | 抽 AudioConfig 统一两端常量 | 小 |
| **B** | 蓝牙传输层 | BluetoothSocket 传输 | ~300行 |
| **B** | USB ADB forward 传输层 | TCP 隧道传输 | ~150行 |
| **B** | Win 系统托盘 | 关窗口不关程序 | 1天 |
| **B** | mDNS 端到端验证 | 串行化修复后未实测连接 | 测试 |

---

## 备份信息

| 备份 | 内容 |
|------|------|
| `D:\backup\LAN-Audio-Bridge-bak_20260713_0215` | **全量重构后最新（双端编译验证通过）** |
| `D:\backup\LAN-Audio-Bridge-bak_20260713_0204` | 回滚 Legacy 热点化后 + 项目文档更新后 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1845` | 延迟调优后最新（BufferMs=100, WaveOut=100, Wasapi=50） |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1810` | 断线重连实现后的最新备份（验证通过） |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1808` | 断线重连实现中间版 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1805` | 断线重连实现起点 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1741` | 协议头抽象后（实际 17:41） |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_1741` | 协议头抽象后 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_0415` | 重构+中文注释后 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_0322` | 状态机实现后 |
| `D:\backup\LAN-Audio-Bridge-bak_20260712_0228` | 状态机实现起点 |
| `D:\backup\LAN-Audio-Bridge-bak_20260709_2315` | 音质优化 + WaveOutEvent 热切回归版 |
| `D:\backup\LAN-Audio-Bridge-bak_20260709_2255` | 音质优化前热切回归版 |
| `D:\backup\LAN-Audio-Bridge-bak_20260709_2244` | 最旧备份 |
