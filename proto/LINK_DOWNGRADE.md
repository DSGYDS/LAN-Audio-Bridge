# LAN Audio Bridge — 四级链路降级架构

> 定稿：2026-07-12
> 优先级：S 级（断线重连后下一大功能）

---

## 链路优先级

```
🥇 WiFi 直连    ← 默认链路，UDP over LAN
🥈 WiFi Direct  ← AP 隔离/无路由器时 P2P 打穿
🥉 蓝牙          ← 无可用的 IP 网络时降级
🎯 USB          ← 有线传输，最稳但需物理连接
```

从高到低自动降级。高一级恢复后自动升回。

---

## 传输层架构

```
┌────────────────────────────────────────────────┐
│               Application Layer                │
│       (Opus 编解码 / PacketHeader / 状态机)      │
├────────────────────────────────────────────────┤
│               Transport Abstraction            │
│         ITransportChannel (Send/Receive)       │
├──────────┬──────────┬──────────┬───────────────┤
│  UDP     │  UDP     │  Bluetooth│ USB ADB/TCP  │
│ (WiFi)   │ (P2P)    │  Socket   │              │
└──────────┴──────────┴──────────┴───────────────┘
```

UDP 传输（WiFi 直连 + WiFi Direct）可完全复用现有代码，仅在链路层不同。

---

## 各链路分析

### 🥇 WiFi 直连（已实现）

- 协议：UDP over IP
- 端口：12345（音频）+ 12347（握手）
- 发现：mDNS
- 状态：**完整实现且已验证**

### 🥈 WiFi Direct（P2P）

| 方面 | 说明 |
|------|------|
| Android 端 | `WifiP2pManager` → `createGroup()`，成为 Group Owner<br>自带 DHCP，GO IP = `192.168.49.1`<br>客户端拿到 `192.168.49.x` |
| Windows 端 | **第一阶段**：用户手动连接 P2P 网络（当普通 WiFi 连）<br>**第二阶段**：WinRT `WiFiDirect` API 自动连接（需确认网卡兼容性） |
| 传输层 | **完全复用现有 UDP 代码**，连上后 mDNS 发现 + PacketHeader 一切照旧 |
| 改动量 | Android 新增 ~200 行建链逻辑，Windows 端第一阶段 0 行传输代码 |

### 🥉 蓝牙

| 方面 | 说明 |
|------|------|
| 传输协议 | BluetoothSocket（RFCOMM），Android 做 Server，Windows 做 Client |
| 带宽 | ~2Mbps 实际可用，128kbps Opus 勉强够但可能不稳 |
| 需适配 | 无法复用 UDP 代码，需实现 `BluetoothTransport`（Send/Receive） |
| 发现 | 蓝牙配对 → RFCOMM 连接 |
| 改动量 | 两端新增传输层实现，~300 行 + |

### 🎯 USB

| 方面 | 说明 |
|------|------|
| 方式 A | ADB forward TCP 端口隧道（无需 ROOT，需 USB 调试开启） |
| 方式 B | USB accessory 模式（AOA 2.0，无需 ADB，但 Android 端需支持） |
| 方式 C | WinUSB + 批量传输（最低延迟，但两端都需写驱动层） |
| 推荐 | **方式 A（ADB forward）** 最易实现，开发阶段就能用 |
| 传输层 | 走 TCP，需实现 `TcpTransport`（Send/Receive） |
| 改动量 | 较低（ADB forward 端对端 TCP，与 UDP 接口类似） |

---

## 自动降级逻辑

```
STREAMING（WiFi）
   │
   ├─ WiFi 断开 → RECOVERING
   │    ├─ 先试 WiFi 直连（mDNS 扫）→ 成功则升回 🥇
   │    ├─ 失败 → 启动 WiFi Direct P2P
   │    │    ├─ 成功 → 切到 P2P 链路，保持 STREAMING
   │    │    └─ 失败 → 降蓝牙
   │    └─ 蓝牙失败 → 降 USB（如果已连接）
   │
   └─ 在低级别链路时，定期 Probe 上级链路
        → 高一级可用则升回
```

降级触发沿用现有 `ReconnectionManager` 的五路触发机制。升回逻辑加一个 `LinkProbe` 定时器，在非 🥇 链路时每 30s 尝试探测 WiFi 连通性。

---

## 实现阶段规划

| 阶段 | 内容 | 传输层改动 | 优先级 |
|------|------|-----------|--------|
| **一** | WiFi Direct 建链（Android 端 `WifiP2pManager`） | 0 行 | **S** |
| **二** | 降级/升回逻辑（LinkProbe + 链路切换器） | 0 行 | S |
| **三** | 蓝牙传输层（BluetoothSocket） | ~300 行 | A |
| **四** | USB ADB forward 传输层 | ~150 行 | A |
| **五** | Windows 端 WiFi Direct 自动连接（WinRT） | 0 行（建链，非传输） | B |
