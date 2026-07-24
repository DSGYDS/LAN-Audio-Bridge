package com.lanbridge.app.core.adapters

import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.ITransport
import kotlinx.coroutines.*
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.UUID

/**
 * BluetoothTransport — ITransport 的 RFCOMM 实现（Android 端，Client 角色）
 *
 * 职责：主动连接 Windows RFCOMM Server，在字节流上实现 PacketHeader 帧分割。
 *
 * 帧分割协议（与 Windows 端一致）：
 *   发送：直接写入 [15B header + payload] 完整字节数组
 *   接收：读 15B → 解析 PayloadLength → 再读 payload → 触发 onPacketReceived
 *
 * 生命周期：
 *   connectTo(device) → 连接到 Windows 蓝牙服务
 *   连接建立后自动启动帧分割读取循环
 *   disconnect() → 关闭连接
 */
class BluetoothTransport : ITransport {

    companion object {
        private const val TAG = "BluetoothTransport"
        private const val HEADER_SIZE = 15
        private const val MAX_PAYLOAD_SIZE = 65536

        /** 自定义服务 UUID（与 Windows 端一致） */
        val SERVICE_UUID: UUID = UUID.fromString("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")
    }

    private var socket: BluetoothSocket? = null
    private var inputStream: InputStream? = null
    private var outputStream: OutputStream? = null
    private var scope: CoroutineScope? = null
    @Volatile private var _isConnected = false

    // ── ITransport 实现 ──

    override var onPacketReceived: ((ByteArray) -> Unit)? = null
    override val isConnected: Boolean get() = _isConnected
    override val type: TransportType = TransportType.Bluetooth

    /**
     * 连接到 Windows RFCOMM Server。
     * 使用 createInsecureRfcommSocketToServiceRecord（不触发系统配对弹窗，依赖已有配对）。
     * @param device 已配对的 Windows 蓝牙设备
     * @return true=连接成功
     */
    fun connectTo(device: BluetoothDevice): Boolean {
        if (_isConnected) return true

        return try {
            val s = device.createInsecureRfcommSocketToServiceRecord(SERVICE_UUID)
            s.connect()
            socket = s
            inputStream = s.inputStream
            outputStream = s.outputStream
            _isConnected = true

            scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
            scope!!.launch { receiveLoop() }

            Log.i(TAG, "Connected to Windows RFCOMM: ${device.address}")
            true
        } catch (e: IOException) {
            Log.e(TAG, "connectTo failed: ${e.message}")
            closeSocket()
            false
        } catch (e: SecurityException) {
            Log.e(TAG, "Bluetooth permission denied: ${e.message}")
            false
        }
    }

    /** ITransport.connect — 蓝牙链路使用 connectTo(device)，此方法为接口兼容保留 */
    override suspend fun connect() { }

    /** 断开 RFCOMM 连接，停止读取循环，释放流和 socket */
    override suspend fun disconnect() {
        if (!_isConnected) return
        _isConnected = false

        scope?.cancel()
        scope = null
        closeSocket()

        Log.i(TAG, "Disconnected")
    }

    override suspend fun send(data: ByteArray) {
        sendBlocking(data)
    }

    /**
     * 阻塞发送（供非协程的音频采集线程使用）
     * 写入完整包字节数组到 RFCOMM 流，synchronized 保证线程安全
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

    // ── 流式帧分割接收循环（与 Windows 端 ReadLoopAsync 逻辑对称） ──

    private suspend fun receiveLoop() = withContext(Dispatchers.IO) {
        val headerBuf = ByteArray(HEADER_SIZE)

        while (isActive && _isConnected) {
            try {
                if (!readExact(headerBuf, HEADER_SIZE)) break

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

    private fun readExact(buffer: ByteArray, count: Int): Boolean {
        val input = inputStream ?: return false
        var offset = 0
        while (offset < count) {
            val read = input.read(buffer, offset, count - offset)
            if (read == -1) return false
            offset += read
        }
        return true
    }

    private fun validateHeader(header: ByteArray): Boolean {
        if (header.size < HEADER_SIZE) return false
        val magic = ((header[0].toInt() and 0xFF) shl 24) or
                    ((header[1].toInt() and 0xFF) shl 16) or
                    ((header[2].toInt() and 0xFF) shl 8) or
                    (header[3].toInt() and 0xFF)
        if (magic != 0x4C414242) return false
        return header[4].toInt() == 0x02
    }

    private fun parsePayloadLength(header: ByteArray): Int {
        return ((header[11].toInt() and 0xFF) shl 24) or
               ((header[12].toInt() and 0xFF) shl 16) or
               ((header[13].toInt() and 0xFF) shl 8) or
               (header[14].toInt() and 0xFF)
    }

    private fun closeSocket() {
        try { inputStream?.close() } catch (_: Exception) {}
        try { outputStream?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        inputStream = null
        outputStream = null
        socket = null
    }
}
