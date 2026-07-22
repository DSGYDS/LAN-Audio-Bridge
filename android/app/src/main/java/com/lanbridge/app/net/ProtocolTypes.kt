package com.lanbridge.app.net

/**
 * PacketType 枚举 — 与 PacketHeader 完全解耦
 *
 * 定义所有支持的包类型。
 * PacketHeader 内部不引用此枚举，只编解码 byte。
 */
enum class PacketType(val code: Byte) {
    HELLO(0x01),        // Android→Win: 首次连接握手请求, payload=1B routeMode
    HELLO_ACK(0x02),    // Win→Android: 握手成功确认, payload=无
    HELLO_NACK(0x03),   // Win→Android: 握手拒绝, payload=无
    ROUTE(0x04),        // Android→Win: 推流中切换路由, payload=1B newRouteMode
    ROUTE_ACK(0x05),    // Win→Android: 路由切换确认, payload=无
    AUDIO(0x06),        // Android→Win: 音频帧, payload=纯 Opus
    HEARTBEAT(0x07);    // 双向: 预留心跳, payload=无

    companion object {
        private val map = entries.associateBy { it.code }

        /** 根据字节码查找枚举，找不到返回 null */
        fun fromCode(code: Byte): PacketType? = map[code]
    }
}

/**
 * LinkType — 四级链路类型标识（包头 [6] 字段）
 */
object LinkType {
    const val UNKNOWN: Byte = 0x00
    const val WIFI_LAN: Byte = 0x01
    const val WIFI_DIRECT: Byte = 0x02
    const val BLUETOOTH: Byte = 0x03
    const val USB: Byte = 0x04
}
