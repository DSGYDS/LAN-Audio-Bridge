package com.lanbridge.app.audio

/**
 * AudioEventListener — 音频管线的监听器接口
 *
 * ## 职责
 * 替代之前的 `(ByteArray, Int) -> Unit` 函数引用风格，
 * 提供结构化的回调接口，便于扩展和事件名称自描述。
 *
 * ## 当前回调
 * - onOpusFrame: 每帧 Opus 编码完成后触发（data=Opus负载, seq=帧序号）
 */
interface AudioEventListener {
    /**
     * 一帧 Opus 编码完成
     *
     * @param data Opus 编码数据
     * @param seq 帧序号（从 0 递增）
     */
    fun onOpusFrame(data: ByteArray, seq: Int)
}
