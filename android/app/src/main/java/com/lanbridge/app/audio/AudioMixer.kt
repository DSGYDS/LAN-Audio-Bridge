package com.lanbridge.app.audio

/**
 * AudioMixer — PCM16LE 混音器
 *
 * ## 职责
 * 将两路 PCM16LE 各衰减一半后相加，防止削波。
 * 用于 MODE_MIX（系统音频 + 麦克风混音 → 扬声器）
 *
 * ## 线程安全
 * 无状态，可多线程共用。
 */
object AudioMixer {

    /**
     * 混音两路 PCM16LE
     *
     * 算法：各取一半再相加，即 (a + b) / 2。
     * 输入长度超过 [targetBytes] 时截断，不足时填充 0。
     *
     * @param a 第一路 PCM16LE（麦克风）
     * @param b 第二路 PCM16LE（系统音频）
     * @param targetBytes 输出目标字节数（默认 1920）
     * @return 混音结果
     */
    fun mix(a: ByteArray, b: ByteArray, targetBytes: Int = 1920): ByteArray {
        val len = minOf(a.size, b.size, targetBytes)
        val r = ByteArray(targetBytes)
        for (i in 0 until len step 2) {
            var s1 = (a[i].toInt() and 0xFF) or (a[i + 1].toInt() shl 8)
            var s2 = (b[i].toInt() and 0xFF) or (b[i + 1].toInt() shl 8)
            if (s1 >= 32768) s1 -= 65536
            if (s2 >= 32768) s2 -= 65536
            val m = ((s1 + s2) / 2).coerceIn(-32768, 32767)
            r[i] = (m and 0xFF).toByte()
            r[i + 1] = (m shr 8).toByte()
        }
        return r
    }

    /**
     * 单路静音（全部填 0）
     */
    fun silence(targetBytes: Int = 1920): ByteArray = ByteArray(targetBytes)
}
