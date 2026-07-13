# WiFi Direct — 开发路线图（revised）

> 节奏：P1 PoC → P2 实现 → P3 抽象
> 先有两个能跑的，再抽接口

---

## P1：WiFi Direct PoC ✅（已完成）

| 子项 | 状态 | 备注 |
|------|------|------|
| P1.1 Legacy GO 创建 | ✅ | `IsAutonomousGroupOwnerEnabled=true` 是关键 |
| P1.2 P2P 适配器 IP | ✅ | 192.168.137.1 |
| P1.3 UDP 双向通信 | ✅ | 收发正常，中文/ASCII 全通 |
| P1.4 Android 兼容性 | ✅ | 至少该机型可用 |
| P1.5 WinRT 编译 | ✅ | 双项目通过 |

---

## P2：WiFi Direct 正式实现（不抽象）

**目标：Android App 内扫码 → 连接 P2P → 推流，Windows 端正常接收。**

| 步骤 | 内容 | 涉及文件 | 说明 |
|------|------|---------|------|
| **2.1** | **Windows `WifiDirectPublisher.cs`** | 新增文件，独立类，不实现接口 | 封装 WiFiDirectAdvertisementPublisher + UDP 收发 + Token 生成 |
| **2.2** | **Windows QR 生成 + P2P UI** | `QrCodeHelper.cs` + `MainWindow.axaml/.cs` | QRCoder 生成二维码，P2P 按钮，SSID/IP/Token 信息显示 |
| **2.3** | **HandshakeServer Token 验证** | `HandshakeServer.cs` | HELLO 包多了 token 字段校验 |
| **2.4** | **Android `P2pConnectionManager.kt`** | `transport/P2pConnectionManager.kt` | 扫码 → 连接 P2P WiFi → 等 DHCP IP |
| **2.5** | **Android 扫码入口 + 推流集成** | `MainActivity.kt` + `LanAudioDiscovery.kt` + `UdpSender.kt` | 扫一扫按钮 → 解析二维码 → 连接 → HELLO(带token) → 推流 |
| **2.6** | **端到端联调** | — | 扫码 → 连 → HELLO → 音频流，全部走通 |

### 2.1 ~ 2.6 依赖关系

```
2.1 WifiDirectPublisher
    ↓
2.2 QR + UI ───── 2.3 Token 验证
                         ↓                    2.4 P2pConnectionManager
                         │                          ↓
                         └────── 2.5 Android 集成 ──┘
                                       ↓
                                 2.6 端到端联调
```

---

## P3：抽象 ITransport + 降级框架

**目标：有了 LAN 和 WiFi Direct 两套实现后，提取共性，抽象接口。**

| 步骤 | 内容 | 涉及文件 | 前置 |
|------|------|---------|------|
| **3.1** | **提取 `ITransport` 接口** | `Transport/ITransport.cs/.kt` | P2 ✅（两套实现） |
| **3.2** | **`LanTransport` 封装现有 UDP** | `Transport/LanTransport.cs/.kt` | 3.1 |
| **3.3** | **`WifiDirectTransport` 实现接口** | `Transport/WifiDirectTransport.cs` | 3.1 |
| **3.4** | **`TransportManager`** | `Transport/TransportManager.cs/.kt` | 3.2 + 3.3 |
| **3.5** | **自动降级/升回逻辑** | `TransportManager.cs` | 3.4 |
| **3.6** | **蓝牙链路调研** | `proto/BLUETOOTH.md` | 3.5 |
| **3.7** | **USB 链路调研** | `proto/USB.md` | 3.5 |

---

## 总览

```
P1  PoC（不抽象）          ✅ 已完成
 │
P2  正式实现（不抽象）       ← 现在在这里
 ├── 2.1 WifiDirectPublisher (Windows)
 ├── 2.2 QR + UI (Windows)
 ├── 2.3 Token 验证 (Windows)
 ├── 2.4 P2pConnectionManager (Android)
 ├── 2.5 Android 集成
 └── 2.6 端到端联调
 │
P3  抽象（两套实现后）
 ├── 3.1 ITransport 接口
 ├── 3.2 LanTransport 封装
 ├── 3.3 WifiDirectTransport 实现接口
 ├── 3.4 TransportManager
 ├── 3.5 自动降级/升回
 ├── 3.6 蓝牙
 └── 3.7 USB
```
