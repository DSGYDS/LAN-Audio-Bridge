package com.lanbridge.app.core.infrastructure

class Config {
    val audio = AudioSection()
    val network = NetworkSection()

    class AudioSection {
        var sampleRate: Int = 48000
        var channels: Int = 1
        var frameMs: Int = 20
        var bufferMs: Int = 100
        var waveOutLatency: Int = 100
        var wasapiLatency: Int = 50
        var bitRate: Int = 128000
        var complexity: Int = 10
        var fec: Boolean = true
        var micBufferMultiplier: Float = 2.0f
        var sysAudioBufferMultiplier: Float = 4.0f
    }

    class NetworkSection {
        var audioPort: Int = 12345
        var handshakePort: Int = 12347
        var audioTimeoutMs: Int = 3000
        var maxRetries: Int = 5
        var retryDelayMs: Int = 1000
    }

    companion object {
        val DEFAULT: Config = Config()
    }
}
