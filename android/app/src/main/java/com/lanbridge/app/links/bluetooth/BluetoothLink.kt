package com.lanbridge.app.links.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import com.lanbridge.app.ConnectionState
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.StreamingService
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.core.adapters.BluetoothTransport
import com.lanbridge.app.core.adapters.PacketHeaderAdapter
import com.lanbridge.app.core.infrastructure.Log
import com.lanbridge.app.core.interfaces.Packet
import com.lanbridge.app.links.ILink
import com.lanbridge.app.links.LinkManager
import com.lanbridge.app.links.LinkParams
import com.lanbridge.app.net.LinkType
import com.lanbridge.app.net.PacketType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * BluetoothLink — 蓝牙 RFCOMM 链路（Android 端，主动发起方）
 *
 * 职责：发现已配对 Windows 设备 → 连接 RFCOMM → 发 HELLO → 推流。
 * 与 LAN 模式对称：手机点按钮主动发起连接。
 *
 * 握手方向：Android 发 HELLO(token) → Windows 校验 → 回 HELLO_ACK(route)
 * 数据通路：AudioPipeline → EncodeSender → BluetoothTransport.sendBlocking()
 */
class BluetoothLink(
    private val context: Context,
    private val pipe: AudioPipeline,
    private val stateManager: ConnectionStateManager
) : ILink {

    companion object {
        private const val TAG = "BluetoothLink"
        const val LINK_TYPE_ID: Byte = LinkType.BLUETOOTH
    }

    // ── 子模块 ──
    private val connectMutex = Mutex()
    private var btTransport: BluetoothTransport? = null

    // ── ILink 状态 ──
    @Volatile override var isStreaming = false
        private set
    override var onStatusChanged: ((String) -> Unit)? = null
    override var onStreamingChanged: ((Boolean) -> Unit)? = null

    @Volatile var currentRoute: Int = 0
        private set

    // ── ILink 实现 ──

    /**
     * 连接蓝牙链路（用户点击“蓝牙直连”触发）
     * 流程：查找已配对电脑 → RFCOMM 连接 → 发 HELLO 握手 → 启动推流
     */

    @SuppressLint("MissingPermission")
    override suspend fun connect(params: LinkParams): Boolean = connectMutex.withLock {
        currentRoute = params.route
        onStatusChanged?.invoke("蓝牙：搜索已配对的电脑...")
        stateManager.update(ConnectionState.CONNECTING)

        // 1. 查找已配对的 Windows 设备
        val device = findPairedWindowsDevice()
        if (device == null) {
            onStatusChanged?.invoke("蓝牙：未找到已配对的电脑（请先在系统设置中配对）")
            stateManager.update(ConnectionState.ERROR)
            return@withLock false
        }
        Log.i(TAG, "Found paired device: ${device.name} (${device.address})")
        onStatusChanged?.invoke("蓝牙：连接 ${device.name}...")

        // 2. 建立 RFCOMM 连接
        val transport = BluetoothTransport()
        val connected = withContext(Dispatchers.IO) { transport.connectTo(device) }
        if (!connected) {
            onStatusChanged?.invoke("蓝牙：连接失败（电脑端是否已启动？）")
            stateManager.update(ConnectionState.ERROR)
            return@withLock false
        }
        btTransport = transport
        onStatusChanged?.invoke("蓝牙：已连接，握手中...")

        // 3. 主动发 HELLO → 等待 ACK
        val ackRoute = withContext(Dispatchers.IO) {
            BtHandshakeClient.sendHelloAndWaitForAck(transport, params.route)
        }
        if (ackRoute < 0) {
            onStatusChanged?.invoke("蓝牙：握手失败（电脑未响应）")
            stateManager.update(ConnectionState.ERROR)
            disconnect()
            return@withLock false
        }

        stateManager.update(ConnectionState.CONNECTED)
        onStatusChanged?.invoke("蓝牙：握手成功 ✓ 准备推流")

        // 4. 启动推流（注入 BluetoothTransport）
        val capMode = LinkManager.routeToCapture(params.route)
        val ok = withContext(Dispatchers.IO) {
            pipe.currentLinkType = LINK_TYPE_ID
            pipe.startStreamingWithTransport(transport, capMode, params.proj, context)
        }

        if (ok) {
            isStreaming = true
            pipe.onFirstFrame = { stateManager.update(ConnectionState.STREAMING) }
            onStatusChanged?.invoke("蓝牙推流中：路线${params.route + 1}")
            onStreamingChanged?.invoke(true)
            context.startForegroundService(Intent(context, StreamingService::class.java))
        } else {
            onStatusChanged?.invoke("蓝牙：启动推流失败")
            stateManager.update(ConnectionState.ERROR)
            disconnect()
        }
        ok
    }

    /** 推流中热切路线：切换采集模式 + 发送 ROUTE 包通知 Windows 切换 AudioRouter */
    override suspend fun sendRouteUpdate(route: Int, proj: MediaProjection?): Boolean {
        if (!isStreaming) { currentRoute = route; return true }
        currentRoute = route

        val capMode = LinkManager.routeToCapture(route)
        val ok = withContext(Dispatchers.IO) { pipe.switchMode(capMode, proj, context) }
        if (!ok) { onStatusChanged?.invoke("需先授权系统音频"); return false }

        // 发送 ROUTE 包到 Windows
        val transport = btTransport ?: return false
        withContext(Dispatchers.IO) {
            val protocol = PacketHeaderAdapter()
            val payload = byteArrayOf(route.toByte())
            val packet = Packet(PacketType.ROUTE, LINK_TYPE_ID, 0.toUShort(), payload)
            transport.sendBlocking(protocol.encode(packet))
        }
        return true
    }

    /** 断开蓝牙链路：停止推流 + 关闭 RFCOMM + 状态回退 */
    override fun disconnect() {
        context.stopService(Intent(context, StreamingService::class.java))
        pipe.stopStreaming()

        btTransport?.let { t ->
            kotlinx.coroutines.runBlocking { t.disconnect() }
        }
        btTransport = null

        isStreaming = false
        stateManager.update(ConnectionState.DISCONNECTED)
        onStatusChanged?.invoke("蓝牙：已停止")
        onStreamingChanged?.invoke(false)
    }

    // ── 发现已配对设备（按蓝牙设备类过滤，只连电脑类设备） ──

    @SuppressLint("MissingPermission")
    private fun findPairedWindowsDevice(): BluetoothDevice? {
        val adapter = BluetoothAdapter.getDefaultAdapter() ?: return null
        if (!adapter.isEnabled) return null

        val bondedDevices = adapter.bondedDevices ?: return null

        // 优先找电脑类设备（Major Class = COMPUTER）
        val computer = bondedDevices.firstOrNull { device ->
            device.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.COMPUTER
        }
        if (computer != null) return computer

        // 找不到电脑类设备时返回 null（不连耳机/音箱）
        Log.w(TAG, "No COMPUTER-class device found in ${bondedDevices.size} paired devices")
        return null
    }

}
