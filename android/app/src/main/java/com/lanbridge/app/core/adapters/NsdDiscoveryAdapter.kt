package com.lanbridge.app.core.adapters

import android.content.Context
import com.lanbridge.app.core.enums.TransportType
import com.lanbridge.app.core.interfaces.IDiscovery
import com.lanbridge.app.core.models.DeviceInfo
import com.lanbridge.app.net.LanAudioDiscovery

/**
 * NsdDiscoveryAdapter — mDNS 设备发现适配器
 *
 * 包裹 [LanAudioDiscovery]，实现 [IDiscovery]。
 * 将 NsdManager 的发现回调桥接到统一接口。
 */
class NsdDiscoveryAdapter(
    private val context: Context
) : IDiscovery {

    private var discovery: LanAudioDiscovery? = null

    override var onDeviceFound: ((DeviceInfo) -> Unit)? = null
    override var onDeviceLost: ((DeviceInfo) -> Unit)? = null

    override fun start() {
        if (discovery != null) return

        val d = LanAudioDiscovery(context)
        d.setOnDeviceFound { device ->
            onDeviceFound?.invoke(
                DeviceInfo(
                    name = device.name,
                    ip = device.host,
                    port = device.port,
                    transport = TransportType.Udp
                )
            )
        }
        d.setOnDeviceLost { name ->
            onDeviceLost?.invoke(
                DeviceInfo(
                    name = name,
                    ip = "",
                    port = 0,
                    transport = TransportType.Udp
                )
            )
        }
        d.startScan()
        discovery = d
    }

    override fun stop() {
        discovery?.stopScan()
        discovery = null
    }
}
