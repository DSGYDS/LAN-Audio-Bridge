package com.lanbridge.app.net

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * PacketHeader — LAN Audio Bridge 通信协议通用包头
 *
 * ## 职责
 * 仅负责 14B 包头的编解码，不含任何业务逻辑。
 * Sequence 的维护由发送方业务层负责。
 *
 * ## 格式（14 字节）
 * [0-3]   Magic:        0x4C414242
 * [4]     Version:      0x01
 * [5]     Type:         包类型
 * [6-9]   Sequence:     uint32 BE
 * [10-13] PayloadLength: uint32 BE
 *
 * ## 设计约束
 * - 无状态（Stateless）：不保存 Sequence、Socket 或任何运行时状态
 * - 纯工具类：只提供 Encode/Decode 静态方法
 */
object PacketHeader {

    /** 魔数 - 用于快速判断"这是我的协议包吗" */
    const val MAGIC: Int = 0x4C414242

    /** 当前协议版本 */
    const val CURRENT_VERSION: Byte = 0x01

    /** 包头固定长度 */
    const val HEADER_SIZE: Int = 14

    /**
     * 编码包头（推荐形式）
     *
     * Version 内部写死为 [CURRENT_VERSION]，业务层无需关心。
     * 返回 byte[14]，调用方自行在末尾拼接 Payload。
     */
    fun encodeHeader(type: Byte, seq: Int, payloadLen: Int): ByteArray {
        val buf = ByteBuffer.allocate(HEADER_SIZE)
        buf.order(ByteOrder.BIG_ENDIAN)
        buf.putInt(MAGIC)           // 0-3: Magic
        buf.put(CURRENT_VERSION)    // 4: Version
        buf.put(type)               // 5: Type
        buf.putInt(seq)             // 6-9: Sequence (uint32)
        buf.putInt(payloadLen)      // 10-13: PayloadLength
        return buf.array()
    }

    /**
     * 解码包头
     *
     * 校验规则（任一失败返回 false）：
     * 1. Magic 是否匹配
     * 2. Version 是否匹配
     * 3. PayloadLength 是否等于 data.size - HEADER_SIZE
     *
     * 校验失败时调用方应丢弃整个包，不做截断或容错。
     */
    fun tryDecode(data: ByteArray): PacketHeaderInfo? {
        if (data.size < HEADER_SIZE) return null

        val buf = ByteBuffer.wrap(data)
        buf.order(ByteOrder.BIG_ENDIAN)

        // 校验 Magic
        val magic = buf.int
        if (magic != MAGIC) return null

        // 校验 Version
        val version = buf.get()
        if (version != CURRENT_VERSION) return null

        val type = buf.get()
        val seq = buf.getInt()
        val payloadLen = buf.getInt()

        // 校验 PayloadLength
        val actualPayloadLen = data.size - HEADER_SIZE
        if (payloadLen != actualPayloadLen) return null

        return PacketHeaderInfo(type, seq, payloadLen)
    }
}

/**
 * 包头解码结果（纯数据容器）
 */
data class PacketHeaderInfo(
    val type: Byte,
    val seq: Int,
    val payloadLen: Int
)
