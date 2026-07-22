package com.lanbridge.app.core.interfaces

import com.lanbridge.app.core.enums.StreamType

/**
 * IDataChannel — 通用数据通道接口
 *
 * 抽象底层传输能力，可承载不同类型的流式数据。
 * 未来投屏、文件传输、剪切板同步均复用此接口，仅需注册不同的 IStreamHandler。
 *
 * 设计原则：
 *   - 通道只负责"搬运数据"，不关心数据内容
 *   - 通过 registerHandler 注册处理器实现插件化
 *   - 当前为单向通道（send），预留双向扩展点（receive）
 *   - 音频管线在 P5 中改为 IStreamHandler 实现注册到通道
 */
interface IDataChannel {
    /** 通道唯一标识 */
    val channelId: String

    /** 通道承载的流类型 */
    val streamType: StreamType

    /** 通道是否已打开 */
    val isOpen: Boolean

    /** 打开通道（建立底层传输连接） */
    suspend fun open()

    /** 关闭通道（释放底层传输连接） */
    suspend fun close()

    /** 发送数据（单向：本端 → 远端） */
    suspend fun send(data: ByteArray)

    /**
     * 接收数据（预留双向扩展点）。
     * 当前单向模式下抛出 UnsupportedOperationException。
     * 未来双向通道（如剪切板同步）实现此方法。
     */
    suspend fun receive(): ByteArray

    /** 注册流处理器（插件化：一个通道可挂载一个处理器） */
    fun registerHandler(handler: IStreamHandler)

    /** 卸载流处理器 */
    fun unregisterHandler()

    /** 通道状态变化时回调（true=已打开，false=已关闭） */
    var onStateChanged: ((Boolean) -> Unit)?
}
