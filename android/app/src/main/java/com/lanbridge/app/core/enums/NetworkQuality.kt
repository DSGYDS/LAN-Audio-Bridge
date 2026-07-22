package com.lanbridge.app.core.enums

/**
 * 网络质量等级枚举
 */
enum class NetworkQuality {
    /** 未知 */
    Unknown,

    /** 优秀（局域网低延迟） */
    Excellent,

    /** 良好（WiFi 已连接且互联网可达） */
    Good,

    /** 较差（仅蜂窝数据或信号弱） */
    Poor,

    /** 已断开 */
    Disconnected
}
