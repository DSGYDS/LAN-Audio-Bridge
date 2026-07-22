namespace LanAudioBridge.Desktop;

/// <summary>
/// PacketType 枚举 — 与 PacketHeader 完全解耦。
/// 定义所有支持的包类型。
/// PacketHeader 内部不引用此枚举，只编解码 byte。
/// </summary>
public enum PacketType : byte
{
    Hello     = 0x01,   // Android→Win: 首次连接握手请求, payload=1B routeMode
    HelloAck  = 0x02,   // Win→Android: 握手成功确认, payload=无
    HelloNack = 0x03,   // Win→Android: 握手拒绝, payload=无
    Route     = 0x04,   // Android→Win: 推流中切换路由, payload=1B newRouteMode
    RouteAck  = 0x05,   // Win→Android: 路由切换确认, payload=无
    Audio     = 0x06,   // Android→Win: 音频帧, payload=纯 Opus
}

// 链路类型标识已迁移到各链路独立文件：
//   Links/WifiLan/WifiLanLink.cs     → LinkTypeId = 0x01
//   Links/WifiDirect/WifiDirectLink.cs → LinkTypeId = 0x02
//   Links/Bluetooth/BluetoothLink.cs  → LinkTypeId = 0x03
//   Links/Usb/UsbLink.cs             → LinkTypeId = 0x04
