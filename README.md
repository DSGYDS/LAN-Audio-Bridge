# LAN Audio Bridge 🎧

**让手机音频零配置、低延迟、多模式地传到电脑播放。**

刷抖音不想外放、打游戏想用手机当无线麦克风、直播需要游戏声+解说同时采集——打开 App 即用，无需插线、无需蓝牙配对、无需复杂的网络设置。

---

## ✨ 功能介绍

### 四条音频管线

| 模式 | 手机采集 | 电脑输出 | 场景 |
|------|---------|---------|------|
| 🔊 Speaker | 系统音频（媒体/游戏） | 扬声器 | 刷抖音不外放 |
| 🎛️ Monitor | 系统音频 + 麦克风混音 | 扬声器 | 直播/录屏解说 |
| 🎤 Virtual Mic | 麦克风 | 虚拟麦克风 | 手机当无线麦 |
| 🎮 Shenanigans | 系统音频 | 虚拟麦克风 | 给队友放手机音乐 |

- **热切换**：推流中任意切换模式，无需停止
- **自动发现**：打开电脑端，手机自动发现，点击即连
- **~200ms 端到端延迟**，Opus 128kbps 高音质

---

## 📱 技术架构

| 端 | 语言 | UI | 音频 | 编解码 | 发现 |
|---|------|----|------|--------|------|
| Android | Kotlin | Jetpack Compose | AudioRecord / MediaProjection | Concentus (Opus) | NsdManager (mDNS) |
| Windows | C# | Avalonia | NAudio / VB-CABLE | Concentus | Makaretu.Dns |
| iOS ⏳ | Swift | SwiftUI | ReplayKit | libopus | Bonjour |
| macOS ⏳ | Swift | SwiftUI | CoreAudio / BlackHole | libopus | Bonjour |

**传输**：UDP 12345（音频）+ 12347（控制信令），14B PacketHeader 协议  
**音频**：48kHz / 16bit / 单声道 / 20ms帧 / Opus 128kbps / FEC / CVBR

---

## 🚀 快速开始

### Windows 端（接收端）

```bash
cd windows/LanAudioBridge.Desktop
dotnet build -c Release
# 运行后等待手机连接，端口 12345（音频）+ 12347（握手）
```

### Android 端（采集端）

```bash
cd android
# 首次需要修改 gradle-wrapper.properties 中 distributionUrl 为远程 Gradle 地址
./gradlew assembleDebug
# 安装 APK 后打开，自动发现局域网内电脑
```

### 使用流程

1. Windows 端启动，显示"就绪"
2. Android 端自动发现电脑，点击连接
3. 选择模式开始推流

---

## 🏗️ 项目状态

> ⚠️ 当前处于 **PoC 阶段**：核心功能可用，但距离可发布产品还有差距。

| 阶段 | 进度 | 说明 |
|------|------|------|
| Phase 1-6 | ✅ 已完成 | 核心音频链路、四模式管线、mDNS发现、断线重连 |
| 传化层 P0-P3 | ✅ 已完成 | Core 接口目录、3 接口定义、Infrastructure、Factory |
| 传化层 P4 | ⚠️ 待重新实施 | 核心引擎接入 ITransport 接口（上次改出问题已回滚） |
| Phase 7-10 | 🔲 待开始 | 四级链路降级、UI打磨、苹果端、打包发布 |

详细进度和已知问题见 [项目进度存档.md](项目进度存档.md)。

---

## 🔧 当前已知问题

- Android Gradle Wrapper 写死本地路径，**他人无法直接编译 Android 端**（需手动修改）
- `proguard-rules.pro` 缺失但被引用
- 连接状态机 UI 显示与真实状态脱节
- FEC 已开启但接收端未真正利用（`decodeFec` 传 false）
- 无配对/加密安全机制（局域网工具，优先级低）
- 系统音频采集强制锁媒体音量为 0
- 无真正系统托盘图标

---

## 🗺️ 开发路线

1. **S级**：修复构建问题、修复状态机
2. **A级**：P4 重构（AudioPipeline 拆分 + Windows AudioEngine 拆分）、修复 FEC 序号检查
3. **B级**：静音方案重做、混音同步、文档同步
4. **C级**：安全机制、系统托盘、后台保活、第二批接口、**四级链路降级**

> 四级链路降级（WiFi Direct / 蓝牙 / USB）是优先级最低的新功能——必须在全部基础工作完成后才值得启动。

详情见 [项目进度存档.md](项目进度存档.md)。

---

## 📦 项目结构

```
LAN-Audio-Bridge/
├── android/           📱 Android 采集端（Kotlin + Jetpack Compose）
├── windows/           💻 Windows 接收端（C# + Avalonia）
├── proto/             📜 协议与技术设计文档
├── poc/ + poc-ui/     🧪 WiFi Direct PoC（保留参考）
├── archive/           📦 VB-CABLE 驱动包
├── memory/            📓 每日开发日志
├── 项目进度存档.md     📋 开发进度、卡点、路线图
└── 局域网音频桥项目计划书.md  📋 完整项目计划书
```

---

## 📃 许可证

MIT License

本项目包含 VB-CABLE 虚拟音频驱动（`archive/libs/VB-Cable/`），其再分发需遵守 VB-Audio 的许可条款。

---

## 💖 支持项目

- GitHub 源码完全免费（MIT 协议）
- 未来打包好的安装包将在爱发电上架 ¥6.9 买断制
