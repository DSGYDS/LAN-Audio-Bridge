package com.lanbridge.app.core.enums

/**
 * 采集源类型枚举
 */
enum class CapturerType {
    /** 麦克风采集 */
    Microphone,

    /** 系统音频采集（MediaProjection） */
    SystemAudio,

    /** 混音（麦克风 + 系统音频） */
    Mixed
}
