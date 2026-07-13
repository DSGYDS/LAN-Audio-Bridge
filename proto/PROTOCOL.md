# LAN Audio Bridge — 通信协议 v1（定稿）

> 最后定稿：2026-07-12
> 状态：✅ 设计完成，可直接开工编码

---

## 一、设计原则

1. **统一包头** — 所有包（握手/路由/音频/心跳）共用同一 PacketHeader 格式
2. **Header 纯编解码** — PacketHeader 只负责 Encode/Decode，不包含任何业务逻辑
3. **业务与协议分离** — HELLO/ROUTE/AUDIO 等业务包的组装解析由各自的协议处理代码负责
4. **两个端口保持分离** — 12345 音频 / 12347 握手，只统一 Header 格式，不合并端口
5. **前向兼容** — Version 字段保留，未来协议升级时可优雅拒绝不兼容的旧客户端

---

## 二、PacketHeader 格式（14 字节）

| Offset | Size | 字段 | 说明 |
|--------|------|------|------|
| 0 | 4 | **Magic** | `0x4C414242`（="LABB"），不追求可读 |
| 4 | 1 | **Version** | 协议版本号，当前 `0x01` |
| 5 | 1 | **Type** | 包类型，参见 Type 枚举 |
| 6 | 4 | **Sequence** | uint32 大端序，帧序号。48kHz/20ms 帧率下约 **~2.7 年回卷** |
| 10 | 4 | **PayloadLength** | uint32 大端序，后面 Payload 的字节数（0 表示无 Payload） |
| **14** | | | **Header 到此结束，后面紧跟 Payload** |

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                          Magic (4B)                           |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Version (1B) |   Type (1B)   |                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+                               +
|                      Sequence (uint32 BE)                     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                    PayloadLength (uint32 BE)                   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
|                     Payload (variable)                         |
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

---

## 三、Type 枚举

独立定义在 `ProtocolTypes.cs` / `ProtocolTypes.kt`，与 PacketHeader 完全解耦：

| 值 | 名称 | 方向 | Payload | 说明 |
|----|------|------|---------|------|
| `0x01` | **HELLO** | Android → Windows | 1B: routeMode | 首次连接握手请求 |
| `0x02` | **HELLO_ACK** | Windows → Android | 无 | 握手成功确认 |
| `0x03` | **HELLO_NACK** | Windows → Android | 无 | 握手拒绝 |
| `0x04` | **ROUTE** | Android → Windows | 1B: newRouteMode | 推流中切换路由 |
| `0x05` | **ROUTE_ACK** | Windows → Android | 无 | 路由切换确认 |
| `0x06` | **AUDIO** | Android → Windows | Opus 编码数据 | 音频帧，**纯 Opus，无额外元数据** |
| `0x07` | **HEARTBEAT** | 双向 | 无 | 预留心跳 |

---

## 四、业务包格式

### AUDIO（音频帧）
```
[PacketHeader: type=0x06, seq=N, payloadLen=opusSize]
[纯 Opus 编码数据]
```
- **没有 pcmSize** — Opus Decoder 解码后自知输出长度，pcmSize 是冗余

### HELLO（握手请求）
```
[PacketHeader: type=0x01, seq=0, payloadLen=1]
[routeMode: 1B]   — 0~3
```

### HELLO_ACK（握手确认）
```
[PacketHeader: type=0x02, seq=0, payloadLen=0]
```

### HELLO_NACK（握手拒绝）
```
[PacketHeader: type=0x03, seq=0, payloadLen=0]
```

### ROUTE（路由切换）
```
[PacketHeader: type=0x04, seq=N, payloadLen=1]
[newRouteMode: 1B]   — 0~3
```

### ROUTE_ACK（路由切换确认）
```
[PacketHeader: type=0x05, seq=0, payloadLen=0]
```

---

## 五、PacketHeader 接口定义

### 原则：无状态纯工具类

> **PacketHeader 必须是无状态（Stateless）的工具类，不保存 Sequence，不保存 Socket，不保存任何运行时状态，只负责 Header 的编码与解码。Sequence 的维护由发送方业务层负责。**

### Encode

```csharp
// ❌ 错误：让业务层传 Version
byte[] Encode(byte version, byte type, uint seq, int payloadLen)

// ✅ 正确：Version 用内部常量，业务层只传必须的参数
byte[] Encode(byte type, uint seq, byte[] payload)    // 形式A：一把打包
byte[] Encode(byte type, uint seq, int payloadLen)    // 形式B：只编码头，Payload 业务层自己拼

// 推荐形式B，理由：避免额外拷贝，Payload 可以直接从 Opus 缓冲区拼
byte[] EncodeHeader(byte type, uint seq, int payloadLen)
```

内部写死：`Version = CurrentVersion`（当前 `0x01`）

### Decode

```csharp
bool TryDecode(byte[] data, out byte type, out uint seq, out byte[] payload)
// 或拆开：
bool TryDecodeHeader(byte[] data, out PacketHeaderInfo info)
// 其中 PacketHeaderInfo 包含 type, seq, payloadLen
```

Decode 需校验（任一失败则整体丢弃）：
1. **Magic** — 前 4B 是否等于 `0x4C414242`
2. **Version** — 第 5B 是否等于 `CurrentVersion`
3. **PayloadLength** — 第 10-13B 解析出的 uint32 **必须**等于 `data.Length - HeaderSize`

> **PayloadLength 严格校验规则**：收到一个 UDP 包 → 解析 Header → `Header.PayloadLength == data.Length - HeaderSize`？相等则正常处理；不相等则整个包丢弃。不做截断，不做容错。因为这是私有协议，两端都是自己的代码，不一致只意味着实现 bug 或数据损坏。

### 对比：Encode 版本演进

| 版本 | 实现 | 何时需要 |
|------|------|---------|
| **v1**（当前） | `EncodeHeader(type, seq, payloadLen)` 写死 `Version=CurrentVersion` | 永远使用当前协议版本 |
| **v2**（未来） | 新建 `PacketHeaderV2` 类，或 `CurrentVersion = 2` | 必须向后不兼容时 |

当前不需要考虑 v2，先让 v1 落地。

---

## 六、端口与分工

| 端口 | 用途 | 收/发 |
|------|------|-------|
| **12345** | 音频数据 | Android → Windows（单向） |
| **12347** | 握手/信令 | 双向（HELLO/ROUTE/ACK） |

两个端口使用**相同的 PacketHeader 格式**。当前不合并端口。

---

## 七、Sequence 管理

| 端 | 流 | 维护者 | 初始值 | 递增规则 |
|----|----|--------|--------|---------|
| Android | AUDIO 帧 | UdpSender | 0 | 每帧 +1 |
| Android | HELLO/ROUTE | 业务层 | 0 | 每次握手/切换 +1 |
| Windows | ACK | 业务层 | 0 | 每次回复 +1 |

- PacketHeader 不管理 Sequence
- uint32 在 48kHz/20ms 下约 2.7 年回卷，无需担心

---

## 八、文件改动清单

### 新建（5 个）

| 平台 | 文件 | 内容 |
|------|------|------|
| 文档 | `proto/PROTOCOL.md` | 本文档 |
| Android | `net/PacketHeader.kt` | 纯编解码 + 常量，无业务引用 |
| Android | `net/ProtocolTypes.kt` | `PacketType` 枚举 |
| Windows | `PacketHeader.cs` | 纯编解码 + 常量，无业务引用 |
| Windows | `ProtocolTypes.cs` | `PacketType` 枚举 |

### 修改（4 个）

| 平台 | 文件 | 改动 |
|------|------|------|
| Android | `net/UdpSender.kt` | `sendOpusFrame` 的 8B 自定义头 → 14B PacketHeader；去掉 pcmSize；payload = 纯 Opus |
| Android | `MainActivity.kt` | `doHandshake` / `sendRouteUpdate` 文本 HELLO:2 / ROUTE:1 → PacketHeader 二进制包 |
| Windows | `HandshakeServer.cs` | `Encoding.UTF8.GetString` 文本解析 → PacketHeader 二进制解析 |
| Windows | `AudioEngine.cs` | 跳过 8B 头 → 解析 14B PacketHeader；注意当前代码根本没读 seq/pcmSize，只是跳过，改后同样跳过或存入统计 |

---

## 九、编码注意事项

### C# 端（Windows）

- 大端序用 `IPAddress.HostToNetworkOrder` 或手动移位
- `TryDecode` 返回 false 时，调用方丢弃整包，不做 fallback
- `EncodeHeader` 返回 `byte[14]`，调用方自行拼接 Payload
- 注意 `ArrayPool<byte>` 的使用：当前 AudioEngine 已经用了 `BytePool.Rent`，尽量复用以减少 GC

### Kotlin 端（Android）

- 大端序用 `java.nio.ByteBuffer` 或手动移位
- `PacketHeader` 用 `object`（纯静态方法）或顶层函数
- `ProtocolTypes` 用 `enum class PacketType : Byte`
- UdpSender 去掉 pcmSize 计算和传输
- 注意 `ByteBuffer.allocate(14).order(ByteOrder.BIG_ENDIAN)` 复用问题

---

## 十、验证清单

| # | 验证项 | 方法 |
|---|--------|------|
| 1 | Windows 端编译 0 错误 0 警告 | `dotnet build -c Release` |
| 2 | Android 端编译通过 | `./gradlew assembleDebug` |
| 3 | Android 发 HELLO，Win 收到后回复 HELLO_ACK | 控制台日志确认 |
| 4 | Android 发 ROUTE，Win 回复 ROUTE_ACK | 同上 |
| 5 | 音频流走通，Speaker 出声 | 耳听为实 |
| 6 | 热切正常（HELLO→STREAMING→ROUTE→STREAMING） | 模式切换不掉线 |
| 7 | Version 不匹配的包被丢弃 | 手动改 Version 发送测试 |
| 8 | Magic 不匹配的包被丢弃 | 同上 |
| 9 | PayloadLength 不匹配的包被丢弃 | 同上 |
