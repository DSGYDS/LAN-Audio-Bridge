package com.lanbridge.app.core.interfaces

import com.lanbridge.app.core.enums.StreamType

/**
 * IStreamHandler — 流式数据处理器接口
 *
 * 处理通过 IDataChannel 传输的流式数据。
 * 音频管线（AudioPipeline）在 P5 中改为此接口的实现，
 * 注册到 IDataChannel 后由通道统一调度。
 *
 * 设计原则：
 *   - 处理器只关心"数据处理"，不关心底层传输
 *   - 通过 StreamType 标识处理哪种流
 *   - 未来视频流、文件流各自实现此接口，插件式注册
 *   - 预留双向扩展：onDataReceived 当前仅单向场景不触发
 */
interface IStreamHandler {
    /** 处理器标识（用于日志和调试） */
    val handlerId: String

    /** 处理的流类型 */
    val streamType: StreamType

    /** 处理器是否已激活 */
    val isActive: Boolean

    /**
     * 处理一帧输出数据（单向：本端产生 → 通道发送）。
     * 例：AudioPipeline 编码后的 Opus 帧通过此方法交给通道发送。
     */
    fun onDataToSend(data: ByteArray)

    /**
     * 处理一帧接收数据（预留双向扩展点）。
     * 当前单向发送模式下不调用。
     */
    fun onDataReceived(data: ByteArray)

    /** 流开始（通道打开后调用） */
    fun onStreamStart()

    /** 流结束（通道关闭前调用） */
    fun onStreamStop()

    /** 流错误通知 */
    fun onError(message: String, ex: Exception? = null)
}
