package com.lanbridge.app.core.adapters

import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.ITransport
import kotlinx.coroutines.*
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket

/**
 * UsbTransport — ITransport 的 TCP 实现（Android 端，Server 角色）
 *
 * 职责：监听 localhost:12348，等待 Windows 通过 ADB forward 隧道连入，
 * 在字节流上实现 PacketHeader 帧分割（15B header + payload）。
 *
 * 数据流：
 *   Windows App (TCP Client) → localhost:12348 → [adb forward] → Android:12348 (本 Server)
 *   adb forward 由 Windows 端执行，将 Windows:12348 映射到 Android:12348。
 *
 * 帧分割协议（与 BluetoothTransport / Windows UsbTransport 一致）：
 *   发送：直接写入 [15B header + payload] 完整字节数组
 *   接收：读 15B → 解析 PayloadLength → 再读 payload → 触发 onPacketReceived
 *
 * 生命周期：
 *   startListening() → 启动 TCP ServerSocket 监听（后台协程）
 *   waitForConnection() → 等待 Windows 连入（阻塞）
 *   连接建立后自动启动帧分割读取循环
 *   disconnect() → 关闭当前连接
 *   stopListening() → 关闭 ServerSocket
 *
 * 依赖：USB 链路专属，与 LAN/P2P/蓝牙完全解耦。
 */
class UsbTransport : ITransport {

    companion object {
        private const val TAG = "UsbTransport"

        /** USB 链路 TCP 端口（adb forward 双端一致） */
        const val PORT = 12348

        private const val HEADER_SIZE = 15
        private const val MAX_PAYLOAD_SIZE = 65536
    }

    private var serverSocket: ServerSocket? = null
    private var clientSocket: Socket? = null
    private var inputStream: InputStream? = null
    private var outputStream: OutputStream? = null
    private var scope: CoroutineScope? = null
    @Volatile private var _isConnected = false
    @Volatile private var _listening = false

    // ── ITransport 实现 ──

    override var onPacketReceived: ((ByteArray) -> Unit)? = null
    override val isConnected: Boolean get() = _isConnected
    override val type: TransportType = TransportType.Usb

    /**
     * 启动 TCP Server 监听（后台协程，不阻塞）。
     * 用户点击"USB 直连"时调用，等待 Windows 通过 adb forward 连入。
     */
    fun startListening() {
        if (_listening) return
        _listening = true

        scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        scope!!.launch {
            try {
                serverSocket = ServerSocket(PORT)
                Log.i(TAG, "TCP server listening on port $PORT")
            } catch (e: IOException) {
                Log.e(TAG, "Failed to bind port $PORT: ${e.message}")
                _listening = false
            }
        }
    }

    /**
     * 等待 Windows 连接（阻塞直到有连接或超时）。
     * 连接建立后自动启动帧分割读取循环。
     * @param timeoutMs accept 超时（毫秒），0=无限等待
     * @return true=连接成功
     */
    fun waitForConnection(timeoutMs: Int = 0): Boolean {
        val ss = serverSocket ?: return false

        return try {
            if (timeoutMs > 0) ss.soTimeout = timeoutMs
            val socket = ss.accept()
            clientSocket = socket
            inputStream = socket.getInputStream()
            outputStream = socket.getOutputStream()
            _isConnected = true

            // 启动帧分割读取循环
            scope?.launch { receiveLoop() }

            Log.i(TAG, "Windows connected: ${socket.inetAddress}")
            true
        } catch (e: IOException) {
            Log.w(TAG, "Accept timeout or error: ${e.message}")
            false
        }
    }

    /** ITransport.connect — USB 链路使用 startListening()+waitForConnection()，此方法为接口兼容保留 */
    override suspend fun connect() { }

    /** 断开当前 TCP 连接（不停止监听，可继续等待下一个连接） */
    override suspend fun disconnect() {
        if (!_isConnected) return
        _isConnected = false

        try { inputStream?.close() } catch (_: Exception) {}
        try { outputStream?.close() } catch (_: Exception) {}
        try { clientSocket?.close() } catch (_: Exception) {}
        inputStream = null
        outputStream = null
        clientSocket = null

        Log.i(TAG, "Client disconnected")
    }

    /** 停止监听（关闭 ServerSocket，释放所有资源） */
    fun stopListening() {
        _listening = false
        _isConnected = false
        scope?.cancel()
        scope = null
        try { serverSocket?.close() } catch (_: Exception) {}
        try { clientSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        clientSocket = null
        Log.i(TAG, "TCP server stopped")
    }

    override suspend fun send(data: ByteArray) {
        sendBlocking(data)
    }

    /**
     * 阻塞发送（供非协程的音频采集线程使用）
     * 写入完整包字节数组到 TCP 流，synchronized 保证线程安全
     */
    fun sendBlocking(data: ByteArray) {
        if (!_isConnected) return
        try {
            outputStream?.let { out ->
                synchronized(out) {
                    out.write(data)
                    out.flush()
                }
            }
        } catch (e: IOException) {
            Log.e(TAG, "sendBlocking error: ${e.message}")
            _isConnected = false
        }
    }

    // ── 流式帧分割接收循环（与 Windows UsbTransport / BluetoothTransport 逻辑对称） ──

    private suspend fun receiveLoop() = withContext(Dispatchers.IO) {
        val headerBuf = ByteArray(HEADER_SIZE)

        while (isActive && _isConnected) {
            try {
                if (!readExact(headerBuf, HEADER_SIZE)) break

                // 校验 Magic + Version（不用 PacketHeader.TryDecode，帧分割场景下会误判）
                if (!validateHeader(headerBuf)) {
                    Log.w(TAG, "Invalid header, disconnecting")
                    break
                }

                val payloadLen = parsePayloadLength(headerBuf)
                if (payloadLen < 0 || payloadLen > MAX_PAYLOAD_SIZE) {
                    Log.w(TAG, "Invalid payload length: $payloadLen")
                    break
                }

                val fullPacket = ByteArray(HEADER_SIZE + payloadLen)
                System.arraycopy(headerBuf, 0, fullPacket, 0, HEADER_SIZE)
                if (payloadLen > 0) {
                    val payload = ByteArray(payloadLen)
                    if (!readExact(payload, payloadLen)) break
                    System.arraycopy(payload, 0, fullPacket, HEADER_SIZE, payloadLen)
                }

                onPacketReceived?.invoke(fullPacket)

            } catch (e: CancellationException) { break }
            catch (e: IOException) {
                if (_isConnected) Log.e(TAG, "receiveLoop IO: ${e.message}")
                break
            } catch (e: Exception) {
                if (_isConnected) Log.e(TAG, "receiveLoop error: ${e.message}")
                break
            }
        }

        _isConnected = false
        Log.i(TAG, "receiveLoop exited")
    }

    /** 精确读取 count 字节（流可能分片到达，循环读满） */
    private fun readExact(buffer: ByteArray, count: Int): Boolean {
        val input = inputStream ?: return false
        var offset = 0
        while (offset < count) {
            val read = input.read(buffer, offset, count - offset)
            if (read == -1) return false  // EOF
            offset += read
        }
        return true
    }

    /** 校验 Magic(0x4C414242) + Version(0x02) */
    private fun validateHeader(header: ByteArray): Boolean {
        if (header.size < HEADER_SIZE) return false
        val magic = ((header[0].toInt() and 0xFF) shl 24) or
                    ((header[1].toInt() and 0xFF) shl 16) or
                    ((header[2].toInt() and 0xFF) shl 8) or
                    (header[3].toInt() and 0xFF)
        if (magic != 0x4C414242) return false
        return header[4].toInt() == 0x02
    }

    /** 从 header[11-14] 解析 PayloadLength（大端序 uint32） */
    private fun parsePayloadLength(header: ByteArray): Int {
        return ((header[11].toInt() and 0xFF) shl 24) or
               ((header[12].toInt() and 0xFF) shl 16) or
               ((header[13].toInt() and 0xFF) shl 8) or
               (header[14].toInt() and 0xFF)
    }

    /** 是否正在监听 */
    val isListening: Boolean get() = _listening
}
