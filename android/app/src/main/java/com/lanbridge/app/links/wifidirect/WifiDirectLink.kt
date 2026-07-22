package com.lanbridge.app.links.wifidirect

/**
 * WiFi Direct 链路常量 — P2P 直连（Android 做 GO，Windows 做客户端）
 *
 * 角色：Android 做 Group Owner（自带 DHCP），Windows 做客户端
 * 发现：Android 创建 P2P Group，Windows 轮询发现
 * 握手：Windows → Android HELLO（携带 token），Android 回 HELLO_ACK
 * 传输：UDP 单播（P2P 接口 IP）
 */
object WifiDirectLink {
    /** 链路类型标识（包头 [6] 字段） */
    const val LINK_TYPE_ID: Byte = 0x02

    /** 音频数据端口 */
    const val AUDIO_PORT = 12345

    /** 握手/控制信令端口 */
    const val HANDSHAKE_PORT = 12347

    /** P2P 模式握手超时（ms，链路延迟较高） */
    const val HANDSHAKE_TIMEOUT_MS = 3000L

    /** P2P 握手重发次数（ARP 解析可能丢弃前几包） */
    const val HANDSHAKE_MAX_ATTEMPTS = 3

    /** P2P 本地 IP 等待轮询间隔（ms） */
    const val IP_POLL_INTERVAL_MS = 500L

    /** P2P 本地 IP 等待最大次数 */
    const val IP_POLL_MAX_RETRIES = 16
}
