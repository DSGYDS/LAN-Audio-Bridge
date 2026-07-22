package com.lanbridge.app.audio

/**
 * PCM 拼帧器 — 将任意长度 PCM 数据拼成固定帧长
 *
 * 采集器返回的数据可能是任意长度（取决于 AudioRecord 内部缓冲），
 * 拼帧器将其累积拼成固定 1920 字节（960 采样点 × 2 字节）帧后回调。
 */
class PcmFrameAssembler(private val frameBytes: Int) {
    private val pending = ByteArray(frameBytes)
    private var pendingLen = 0

    /** 喂入数据，每凑满一帧触发 onFrame 回调 */
    fun push(data: ByteArray, onFrame: (ByteArray) -> Unit) {
        var offset = 0
        while (offset < data.size) {
            val n = minOf(frameBytes - pendingLen, data.size - offset)
            System.arraycopy(data, offset, pending, pendingLen, n)
            pendingLen += n
            offset += n
            if (pendingLen == frameBytes) {
                onFrame(pending.copyOf())
                pendingLen = 0
            }
        }
    }

    /** 重置拼帧状态（新会话/切模式时调用） */
    fun reset() { pendingLen = 0 }
}
