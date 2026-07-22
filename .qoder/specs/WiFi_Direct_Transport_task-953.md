# WiFi Direct Transport 实施方案

## 前提条件（已满足）

- ITransport 接口已就位（P4 完成），应用层零改动
- PoC 已验证：Legacy GO 创建、P2P 适配器 IP 获取、UDP 双向通信、WinRT 编译
- 上层协议（PacketHeader / HELLO / ROUTE / AUDIO / 状态机 / FEC / JitterBuffer）完全复用
- QRCoder NuGet 已在 csproj 中引用

## 核心架构

```
Windows (GO)                          Android (Client)
─────────────                         ────────────────
WifiDirectTransport                   WifiDirectManager
  │                                     │
  ├─ WiFiDirectAdvertisementPublisher    ├─ 扫 QR → 得到 device + token
  │   (IsAutonomousGroupOwner=true)     ├─ WifiP2pManager.discoverPeers()
  │                                     ├─ 匹配 device name → connect()
  ├─ 等待 Android 加入 Group            ├─ Group 形成 → WifiP2pInfo
  ├─ 获取 P2P 适配器 IP                 │   → groupOwnerAddress = Windows IP
  ├─ 显示 QR（device + token）          │
  │                                     │
  └─ P2P 网络就绪后：                    └─ P2P 网络就绪后：
     UdpTransport(12345/12347)            UdpTransport(host=GO_IP, 12345/12347)
     ↓ 完全复用现有协议栈                  ↓ 完全复用现有协议栈
```

**关键原则**：WiFi Direct 只负责「建链 + 获取 IP」，建链完成后数据通路 100% 复用现有 UdpTransport。

## QR 码协议（精简版，非热点）

```
LABRIDGE://version=1&transport=wifidirect&device=DESKTOP-ABC&token=8FA29C31
```

| 字段 | 作用 |
|------|------|
| version | 协议版本，当前 1 |
| transport | 链路类型标识 |
| device | Windows 设备名（Android 用于 P2P 发现时匹配） |
| token | 握手认证（HELLO 包携带，Windows 校验） |

不含 SSID/密码/IP —— 这些由 P2P 协议自动协商。

---

## Step 1: Windows — WifiDirectTransport.cs

新建 `d:\LAN-Audio-Bridge\windows\LanAudioBridge.Desktop\Core\Adapters\WifiDirectTransport.cs`

职责：
- 启动 WiFiDirectAdvertisementPublisher（不开 LegacySettings）
- 监听 StatusChanged / ConnectionRequested 事件
- Group 形成后轮询 P2P 适配器 IP（NetworkInterface 枚举 "Wi-Fi Direct" / "P2P"）
- 暴露 `DeviceName`、`Token`、`LocalIp`、`IsGroupFormed` 属性供 UI 使用
- 实现 ITransport：Group 形成后创建 UdpClient 绑定 P2P IP，收发走现有逻辑

关键代码结构：
```csharp
public sealed class WifiDirectTransport : ITransport, IDisposable
{
    public string DeviceName { get; }
    public string Token { get; }
    public string? LocalIp { get; private set; }
    public bool IsGroupFormed { get; private set; }
    public event Action? OnGroupFormed;  // UI 订阅：QR 可显示

    private WiFiDirectAdvertisementPublisher? _publisher;
    private UdpTransport? _udp;  // Group 形成后创建，复用现有 UdpTransport

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // 1. 创建 publisher，设置 IsAutonomousGroupOwnerEnabled = true
        // 2. 不开 LegacySettings（正统 P2P，非热点）
        // 3. 注册 StatusChanged 事件
        // 4. Start()
        // 5. 轮询等待 P2P 适配器 IP（最多 30s）
        // 6. IP 就绪 → 创建 UdpTransport(localPort: 12345, bindTo: p2pIp)
        // 7. IsGroupFormed = true, 触发 OnGroupFormed
    }
}
```

注意：需要 `Windows.Devices.WiFiDirect` WinRT 命名空间。csproj 已 target `net10.0-windows10.0.19041.0`，PoC 已验证可编译。

## Step 2: Windows — QR 码生成

新建 `d:\LAN-Audio-Bridge\windows\LanAudioBridge.Desktop\QrCodeHelper.cs`

- 使用 QRCoder（已引用）生成 Bitmap
- 输入：QR 文本（LABRIDGE://...）
- 输出：Avalonia Bitmap（供 Image 控件显示）

```csharp
public static class QrCodeHelper
{
    public static Bitmap Generate(string content, int pixelsPerModule = 8)
    {
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var bytes = new BitmapByteQRCode(data).GetGraphic(pixelsPerModule);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }
}
```

## Step 3: Windows — MainWindow P2P UI

修改 `MainWindow.axaml` + `MainWindow.axaml.cs`

- 新增「P2P 直连」按钮（与现有 LAN 模式并列）
- 点击后：创建 WifiDirectTransport → ConnectAsync → 等待 GroupFormed → 显示 QR 码
- QR 码下方显示设备名 + 状态（等待连接 / 已连接）
- 连接建立后：将 WifiDirectTransport 内部的 UdpTransport 注入 AudioEngine + HandshakeServer
- Token 验证：HandshakeServer 收到 HELLO 时校验 payload 中的 token 字段

## Step 4: Windows — HandshakeServer Token 校验

修改 `d:\LAN-Audio-Bridge\windows\LanAudioBridge.Desktop\HandshakeServer.cs`

- 新增可选属性 `ExpectedToken`（LAN 模式为 null 不校验，P2P 模式设置 token）
- HELLO 解析后：如果 ExpectedToken != null，从 payload 提取 token 字段比对
- 不匹配 → 回复 HELLO_NACK

HELLO payload 扩展（向后兼容）：
```
[0]    routeMode (1B)
[1-8]  token (8B, ASCII) — 仅 P2P 模式携带
```

## Step 5: Android — WifiDirectManager.kt

新建 `d:\LAN-Audio-Bridge\android\app\src\main\java\com\lanbridge\app\net\WifiDirectManager.kt`

职责：
- 接收 QR 解析结果（device name + token）
- WifiP2pManager.discoverPeers() → 监听 PEERS_CHANGED
- 从 peer list 匹配 device name
- WifiP2pManager.connect(matchedPeer)
- 监听 CONNECTION_CHANGED → requestConnectionInfo → WifiP2pInfo
- groupFormed=true → groupOwnerAddress = Windows IP
- 返回 host IP 给调用方

```kotlin
class WifiDirectManager(private val context: Context) {
    private val p2pManager = context.getSystemService(Context.WIFI_P2P_SERVICE) as WifiP2pManager
    private val channel = p2pManager.initialize(context, context.mainLooper, null)

    suspend fun connectToDevice(targetDeviceName: String): String? {
        // 1. discoverPeers()
        // 2. 等待 PEERS_CHANGED（超时 10s）
        // 3. requestPeers() → 匹配 deviceName
        // 4. connect(peer)
        // 5. 等待 CONNECTION_CHANGED（超时 15s）
        // 6. requestConnectionInfo() → groupOwnerAddress
        // 7. 返回 IP 或 null
    }
}
```

权限（AndroidManifest.xml）：
```xml
<uses-permission android:name="android.permission.NEARBY_WIFI_DEVICES" />  <!-- API 33+ -->
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
<uses-permission android:name="android.permission.CHANGE_WIFI_STATE" />
<uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
```

## Step 6: Android — QR 扫码（CameraX + ML Kit）

新建 `d:\LAN-Audio-Bridge\android\app\src\main\java\com\lanbridge\app\ui\QrScannerScreen.kt`

依赖（build.gradle.kts）：
```kotlin
implementation("androidx.camera:camera-camera2:1.4.0")
implementation("androidx.camera:camera-lifecycle:1.4.0")
implementation("androidx.camera:camera-view:1.4.0")
implementation("com.google.mlkit:barcode-scanning:17.3.0")
```

- Compose 界面：CameraPreview + 实时条码识别
- 识别到 `LABRIDGE://` 前缀 → 解析 → 回调给 MainActivity
- 解析逻辑：

```kotlin
data class LabridgeQrCode(
    val transport: String,
    val deviceName: String,
    val token: String
)

fun parseLabridgeQr(content: String): LabridgeQrCode? {
    if (!content.startsWith("LABRIDGE://")) return null
    val params = content.removePrefix("LABRIDGE://")
        .split("&").associate { val kv = it.split("=", limit=2); kv[0] to (kv.getOrNull(1) ?: "") }
    return LabridgeQrCode(
        transport = params["transport"] ?: return null,
        deviceName = params["device"] ?: return null,
        token = params["token"] ?: ""
    )
}
```

## Step 7: Android — MainActivity 集成

修改 `MainActivity.kt`

- 新增「扫码连接」按钮（与现有 mDNS 自动发现并列）
- 点击 → 打开 QrScannerScreen
- 扫码成功 → WifiDirectManager.connectToDevice(deviceName)
- 连接成功 → 得到 host IP → HandshakeManager.handshake(host, route, token)
- 握手成功 → AudioPipeline.startStreaming(host=host, port=12345)
- 推流开始，与 LAN 模式完全一致

## Step 8: 端到端联调 + 验证

验证清单：
1. Windows 点击 P2P → QR 码显示（< 5s）
2. Android 扫码 → 发现 peer → 连接（< 10s）
3. Group 形成 → 双方获取 IP
4. HELLO(带 token) → HELLO_ACK
5. 音频推流正常（四条管线）
6. 热切正常
7. 断开 → 重连（ReconnectionManager 走 lastKnownHost）
8. LAN 模式不受影响（回归验证）

---

## 文件变更汇总

| 操作 | 文件 | 说明 |
|------|------|------|
| 新建 | `windows/.../Core/Adapters/WifiDirectTransport.cs` | P2P 建链 + ITransport |
| 新建 | `windows/.../QrCodeHelper.cs` | QR 码生成 |
| 修改 | `windows/.../MainWindow.axaml(.cs)` | P2P 按钮 + QR 显示区 |
| 修改 | `windows/.../HandshakeServer.cs` | Token 校验（可选） |
| 新建 | `android/.../net/WifiDirectManager.kt` | P2P 发现/连接 |
| 新建 | `android/.../ui/QrScannerScreen.kt` | 应用内扫码 |
| 修改 | `android/.../MainActivity.kt` | 扫码入口 + P2P 连接流程 |
| 修改 | `android/.../net/HandshakeManager.kt` | HELLO 携带 token |
| 修改 | `android/app/build.gradle.kts` | CameraX + ML Kit 依赖 |
| 修改 | `android/.../AndroidManifest.xml` | P2P + 相机权限 |

## 不改动的文件（完全复用）

- AudioEngine.cs / AudioPipeline.kt（数据通路）
- PacketHeader / PacketHeaderAdapter（协议编解码）
- JitterBuffer.cs（抖动缓冲）
- AudioRouter.cs（四模式分发）
- ConnectionStateManager（状态机）
- ReconnectionManager.kt（断线重连）
- UdpTransport（P2P 网络上的 UDP 传输）

## 风险点

| 风险 | 应对 |
|------|------|
| 非 Legacy 模式下 Android 能否发现 Windows peer | PoC 已验证 Legacy 可行；非 Legacy 需实测，失败则回退 Legacy（功能等价但 UI 表述调整） |
| Windows P2P 适配器 IP 获取延迟 | 轮询 500ms x 60 次（30s 超时），与 PoC 一致 |
| Android 12+ 权限（NEARBY_WIFI_DEVICES） | 运行时动态申请，拒绝则提示用户 |
| P2P 与现有 WiFi 共存（Android 同时连两个） | Android P2P 不影响 STA 连接，双接口并存 |
