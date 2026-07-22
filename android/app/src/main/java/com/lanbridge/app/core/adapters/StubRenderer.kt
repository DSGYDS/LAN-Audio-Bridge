package com.lanbridge.app.core.adapters

import com.lanbridge.app.audio.AudioConfig
import com.lanbridge.app.core.interfaces.IAudioRenderer

/**
 * StubRenderer — 渲染器桩实现（Android 端不渲染音频）
 *
 * Android 是纯发送端，不需要渲染功能。
 * 所有方法为空操作或抛出 NotSupportedException。
 */
class StubRenderer : IAudioRenderer {

    override val isReady: Boolean = false

    override fun prepare(config: AudioConfig): Boolean = false

    override fun play() {}

    override fun stop() {}

    override fun setVolume(volume: Float) {}

    override fun mute(muted: Boolean) {}

    override fun feedPcm(data: ByteArray, offset: Int, count: Int) {}

    override fun release() {}
}
