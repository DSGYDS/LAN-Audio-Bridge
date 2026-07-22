package com.lanbridge.app.core.interfaces

import com.lanbridge.app.audio.AudioConfig
import com.lanbridge.app.core.enums.CapturerType

/**
 * IAudioCapturer — 统一音频采集接口（纯 PCM 层）
 *
 * 采集原始 PCM16LE 帧，不涉及 Opus 编解码。
 * 编解码由应用层 Pipeline 处理。
 *
 * 当前实现：
 *   Android — MicCapturerAdapter / SystemAudioCapturerAdapter
 *   Windows — StubCapturer（Windows 是纯接收端，不采集）
 */
interface IAudioCapturer {
    /** 准备采集器（分配资源） */
    fun prepare(config: AudioConfig): Boolean

    /** 开始采集 */
    fun start(): Boolean

    /** 读取一帧 PCM16LE，返回实际读取的字节数（0 表示无数据） */
    fun readFrame(buffer: ByteArray, offset: Int, count: Int): Int

    /** 停止采集 */
    fun stop()

    /** 释放所有资源 */
    fun release()

    /** HAL 预热（丢弃前几帧，消除冷启动抖动） */
    fun warmup() {}

    /** 看门狗触发时重建采集器（默认返回 false 表示不支持） */
    fun restart(): Boolean = false

    /** 采集源类型 */
    val sourceType: CapturerType
}
