package com.lanbridge.app.links.wifilan

/**
 * WiFi LAN 链路常量 — 同局域网 WiFi 直连
 *
 * 角色：Android 做发送端（client），Windows 做接收端（server）
 * 发现：NsdManager 扫描 _lan-audio._udp
 * 握手：Android → Windows HELLO，Windows 回 HELLO_ACK
 * 传输：UDP 单播
 */
object WifiLanLink {
    /** 链路类型标识（包头 [6] 字段） */
    const val LINK_TYPE_ID: Byte = 0x01

    /** 音频数据端口 */
    const val AUDIO_PORT = 12345

    /** 握手/控制信令端口 */
    const val HANDSHAKE_PORT = 12347

    /** mDNS 服务类型 */
    const val MDNS_SERVICE_TYPE = "_lan-audio._udp"

    /** LAN 模式握手超时（ms） */
    const val HANDSHAKE_TIMEOUT_MS = 500L
}
