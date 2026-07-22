package com.lanbridge.app.core.enums

/**
 * 流类型枚举 — 标识数据通道中承载的流种类
 *
 * 当前仅 Audio 在用，其余为预留扩展。
 */
enum class StreamType {
    /** 音频流（当前唯一在用） */
    Audio,

    /** 视频流（预留：投屏） */
    Video,

    /** 文件流（预留：文件传输） */
    File,

    /** 控制流（预留：剪切板/远程指令） */
    Control
}
