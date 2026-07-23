package com.lanbridge.app.ui

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.lanbridge.app.ConnectionState
import com.lanbridge.app.ConnectionStateManager
import com.lanbridge.app.MediaProjectionService
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.links.LinkManager
import com.lanbridge.app.links.LinkParams
import com.lanbridge.app.net.LanAudioDiscovery
import com.lanbridge.app.net.LinkType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * TestUI — 测试用 UI 布局和交互
 *
 * 职责：路线选择、设备发现、推流控制、扫码直连。
 * 所有链路操作通过 LinkManager 统一入口转发，本文件不含链路实现。
 */
@Composable
fun TestUI() {
    val act = LocalContext.current as ComponentActivity
    var status by remember { mutableStateOf("就绪") }
    var ip by remember { mutableStateOf("192.168.31.90") }
    var streaming by remember { mutableStateOf(false) }
    var proj by remember { mutableStateOf<MediaProjection?>(null) }
    var projReady by remember { mutableStateOf(false) }
    var route by remember { mutableIntStateOf(0) }
    var showScanner by remember { mutableStateOf(false) }

    // ── 核心模块 ──
    val stateManager = remember { ConnectionStateManager() }
    val pipe = remember { AudioPipeline() }
    val linkManager = remember { LinkManager(act, pipe, stateManager) }
    val scope = rememberCoroutineScope()

    // ── 状态订阅（直接订阅各链路回调） ──
    var connState by remember { mutableStateOf(stateManager.state) }
    DisposableEffect(stateManager) {
        stateManager.onStateChanged = { connState = it }
        onDispose { stateManager.onStateChanged = null }
    }
    DisposableEffect(linkManager) {
        linkManager.wifiLan.onStatusChanged = { msg -> status = msg }
        linkManager.wifiLan.onStreamingChanged = { s -> streaming = s }
        linkManager.wifiDirect.onStatusChanged = { msg -> status = msg }
        linkManager.wifiDirect.onStreamingChanged = { s -> streaming = s }
        linkManager.start()
        onDispose { linkManager.stop() }
    }

    // ── mDNS 设备发现（LAN 链路特有） ──
    val discoveredDevices = remember { mutableStateListOf<LanAudioDiscovery.DeviceInfo>() }
    var showDeviceList by remember { mutableStateOf(false) }
    DisposableEffect(linkManager) {
        linkManager.wifiLan.onDeviceFound = { device ->
            val idx = discoveredDevices.indexOfFirst { it.name == device.name }
            if (idx >= 0) discoveredDevices[idx] = device else discoveredDevices.add(device)
        }
        linkManager.wifiLan.onDeviceLost = { name -> discoveredDevices.removeAll { it.name == name } }
        onDispose { }
    }

    // ── P2P 状态（WiFi Direct 链路特有） ──
    val p2pStatus by linkManager.wifiDirect.wifiDirectManager.statusFlow.collectAsState()

    val routeNames = listOf(
        "1. 系统音频 → 扬声器",
        "2. 系统音频 + 麦克风 → 扬声器（混音）",
        "3. 手机麦克风 → 虚拟麦克风",
        "4. 手机系统音频 → 虚拟麦克风"
    )

    // ── 权限请求 ──
    val micPerm = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) scope.launch { status = runTestMic() } else status = "麦克风权限被拒绝"
    }
    val capLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK && result.data != null) {
            try {
                val proj2 = act.getSystemService(MediaProjectionManager::class.java)
                    .getMediaProjection(result.resultCode, result.data!!)
                if (proj2 != null) { proj = proj2; projReady = true; status = "系统音频授权成功" }
                else status = "系统音频授权失败"
            } catch (e: Exception) { status = "错误：${e.message}" }
        } else status = "系统音频授权被取消"
    }

    // ── UI 布局 ──
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Top,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("LAN Audio Bridge 手机推流端", style = MaterialTheme.typography.headlineMedium, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(4.dp))
        Text("局域网音频测试面板", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(20.dp))

        // ── 路线选择 ──
        Text("选择测试路线", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(4.dp))
        Column(Modifier.selectableGroup()) {
            (0..3).forEach { r ->
                Row(
                    modifier = Modifier.fillMaxWidth().height(40.dp).selectable(
                        selected = r == route, onClick = {
                            route = r
                            if (streaming) scope.launch {
                                val needProj = linkManager.routeToCapture(r) != AudioPipeline.MODE_MIC
                                linkManager.sendRouteUpdate(r, if (needProj && projReady) proj else null)
                            }
                        }, role = Role.RadioButton
                    ).padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically
                ) {
                    RadioButton(selected = r == route, onClick = null)
                    Spacer(Modifier.width(8.dp)); Text(routeNames[r])
                }
            }
        }

        // ── 系统音频授权按钮 ──
        if (route == 0 || route == 1 || route == 3) {
            Button(
                onClick = {
                    act.startForegroundService(Intent(act, MediaProjectionService::class.java))
                    capLauncher.launch(act.getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent())
                },
                enabled = !projReady, modifier = Modifier.fillMaxWidth().height(40.dp)
            ) { Text(if (projReady) "系统音频已授权" else "授权系统音频") }
        }

        // ── 状态文字 ──
        Text(status, style = MaterialTheme.typography.bodySmall, textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp))
        Text("● ${connState.toChineseLabel()}", style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center,
            color = when (connState) {
                ConnectionState.IDLE, ConnectionState.DISCONNECTED -> MaterialTheme.colorScheme.onSurfaceVariant
                ConnectionState.CONNECTING, ConnectionState.SEARCHING -> MaterialTheme.colorScheme.tertiary
                ConnectionState.FOUND, ConnectionState.CONNECTED, ConnectionState.STREAMING -> MaterialTheme.colorScheme.primary
                ConnectionState.RECONNECTING -> MaterialTheme.colorScheme.tertiary
                ConnectionState.ERROR -> MaterialTheme.colorScheme.error
            }, modifier = Modifier.fillMaxWidth().padding(bottom = 4.dp))

        HorizontalDivider(); Spacer(Modifier.height(8.dp))

        // ── 设备发现 ──
        Text("连接电脑接收端", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = { showDeviceList = !showDeviceList }, modifier = Modifier.height(36.dp))
            { Text("扫描电脑（${discoveredDevices.size}）") }
            Spacer(Modifier.width(8.dp))
            Button(onClick = { showScanner = true }, modifier = Modifier.height(36.dp))
            { Text("扫码直连") }
            Spacer(Modifier.weight(1f))
            if (discoveredDevices.isNotEmpty()) Text("已发现 ${discoveredDevices.size} 台",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
        }

        // ── P2P 连接进度 ──
        if (p2pStatus != "空闲") {
            Text("P2P: $p2pStatus", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(vertical = 2.dp))
        }
        if (showDeviceList && discoveredDevices.isNotEmpty()) {
            Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column { discoveredDevices.forEach { device ->
                    TextButton(onClick = { ip = device.host; status = "已选择 ${device.name}（${device.host}）"; showDeviceList = false },
                        modifier = Modifier.fillMaxWidth())
                    { Text("${device.name}  (${device.host})", modifier = Modifier.fillMaxWidth()) }
                }}
            }
        }
        Spacer(Modifier.height(4.dp))
        OutlinedTextField(value = ip, onValueChange = { ip = it },
            label = { Text("电脑 IP（可手动填写）") }, singleLine = true,
            modifier = Modifier.fillMaxWidth())

        // ── 开始/停止推流 ──
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            if (streaming) {
                linkManager.disconnect()
            } else {
                if (ContextCompat.checkSelfPermission(act, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
                { micPerm.launch(Manifest.permission.RECORD_AUDIO); return@Button }
                val capMode = linkManager.routeToCapture(route)
                if ((capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX) && !projReady)
                { status = "请先授权系统音频"; return@Button }
                scope.launch {
                    val needProj = capMode != AudioPipeline.MODE_MIC
                    linkManager.reconnect(LinkParams(
                        host = ip, route = route, proj = if (needProj) proj else null
                    ))
                }
            }
        }, modifier = Modifier.fillMaxWidth().height(48.dp))
        { Text(if (streaming) "停止推流" else "开始推流") }

        // ── 快速测试按钮 ──
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            if (ContextCompat.checkSelfPermission(act, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                scope.launch { status = runTestMic() }
            else status = "快速测试需要麦克风权限"
        }, modifier = Modifier.fillMaxWidth().height(40.dp))
        { Text("快速测试麦克风编码（3 秒）") }
    }

    // ── QR 扫码界面 ──
    if (showScanner) {
        QrScannerScreen(
            onScanned = { qr ->
                showScanner = false
                val capMode = linkManager.routeToCapture(route)
                if ((capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX) && !projReady) {
                    status = "请先授权系统音频"
                    return@QrScannerScreen
                }
                scope.launch {
                    val needProj = capMode != AudioPipeline.MODE_MIC
                    linkManager.connect(LinkType.WIFI_DIRECT, LinkParams(
                        token = qr.token, deviceName = qr.deviceName, route = route, proj = if (needProj) proj else null
                    ))
                }
            },
            onDismiss = { showScanner = false }
        )
    }
}

/** 快速 3 秒测试 — 验证采集→编码内环，不依赖网络 */
private suspend fun runTestMic(): String = withContext(Dispatchers.IO) {
    val p = AudioPipeline()
    var frameCount = 0; var inputBytes = 0; var outputBytes = 0
    p.setOnOpusData { data, _ -> frameCount++; inputBytes += AudioPipeline.FRAME_BYTES; outputBytes += data.size }
    if (!p.startStreaming(AudioPipeline.MODE_MIC)) return@withContext "启动失败"
    Thread.sleep(3000)
    p.stopStreaming()
    val ratio = if (outputBytes > 0) "%.1f".format(inputBytes.toDouble() / outputBytes) else "N/A"
    "测试通过：${frameCount} 帧，${inputBytes / 1024}KiB → ${outputBytes / 1024}KiB，压缩约 ${ratio}x"
}
