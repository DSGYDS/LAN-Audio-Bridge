package com.lanbridge.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
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
import com.lanbridge.app.audio.AudioPipeline
import com.lanbridge.app.net.LanAudioDiscovery
import com.lanbridge.app.net.HandshakeManager
import com.lanbridge.app.net.PacketHeader
import com.lanbridge.app.net.PacketType
import com.lanbridge.app.net.ReconnectionManager
import com.lanbridge.app.ui.theme.LanAudioBridgeTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 主界面 — LAN Audio Bridge 手机推流端
 *
 * 四路推流模式，通过 UDP Opus 发送音频到电脑接收端
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            LanAudioBridgeTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) { MainScreen() }
            }
        }
    }
}

@Composable
fun MainScreen() {
    val act = LocalContext.current as ComponentActivity
    var status by remember { mutableStateOf("就绪") }
    var ip by remember { mutableStateOf("192.168.31.90") }
    var streaming by remember { mutableStateOf(false) }
    var proj by remember { mutableStateOf<MediaProjection?>(null) }
    var projReady by remember { mutableStateOf(false) }
    var route by remember { mutableIntStateOf(0) }

    // ── 连接状态管理器（旁路观测层，不影响 streaming 标志） ──
    val stateManager = remember { ConnectionStateManager() }
    var connState by remember { mutableStateOf(stateManager.state) }
    DisposableEffect(stateManager) {
        stateManager.onStateChanged = { connState = it }
        onDispose { stateManager.onStateChanged = null }
    }

    val routeNames = listOf(
        "1. 系统音频 → 扬声器",
        "2. 系统音频 + 麦克风 → 扬声器（混音）",
        "3. 手机麦克风 → 虚拟麦克风",
        "4. 手机系统音频 → 虚拟麦克风"
    )
    // 路线→采集模式映射：路线 0/3 用系统音频, 1 用混音, 2 用麦克风
    fun routeToCapture(r: Int) = when (r) {
        0, 3 -> AudioPipeline.MODE_SYSTEM
        1 -> AudioPipeline.MODE_MIX
        else -> AudioPipeline.MODE_MIC
    }
    val scope = rememberCoroutineScope()
    val pipe = remember { AudioPipeline() }

    // ── 断线重连管理器（旁路观测层，不污染核心链路） ──
    // onRecover 复用已有 doHandshake + pipe.startStreaming，与用户点击"开始"同一代码路径
    val reconnectionManager = remember(pipe, stateManager) {
        ReconnectionManager(
            context = act,
            stateManager = stateManager,
            pipeline = pipe,
            onRecover = onRecover@{ host, mode ->
                val capMode = routeToCapture(mode)
                val handshakeOk = HandshakeManager.handshake(host, mode)
                if (!handshakeOk) return@onRecover false
                val needProj = capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX
                pipe.startStreaming(capMode, if (needProj && projReady) proj else null, act, host)
            }
        )
    }
    DisposableEffect(reconnectionManager) {
        reconnectionManager.start()
        onDispose { reconnectionManager.stop() }
    }

    // ── mDNS 设备发现（事件驱动，不阻塞 UI） ──
    val discoveredDevices = remember { mutableStateListOf<LanAudioDiscovery.DeviceInfo>() }
    var showDeviceList by remember { mutableStateOf(false) }
    val discovery = remember { LanAudioDiscovery(act) }
    LaunchedEffect(Unit) {
        discovery.setOnDeviceFound { device ->
            val idx = discoveredDevices.indexOfFirst { it.name == device.name }
            if (idx >= 0) discoveredDevices[idx] = device else discoveredDevices.add(device)
            if (!streaming) { status = "发现电脑：${device.name}"; stateManager.update(ConnectionState.FOUND) }
        }
        discovery.setOnDeviceLost { name -> discoveredDevices.removeAll { it.name == name } }
        discovery.setOnError { msg -> if (!streaming) status = "设备发现：$msg" }
        discovery.startScan()
    }
    DisposableEffect(Unit) { onDispose { discovery.stopScan() } }

    // ── 权限请求 ──
    val micPerm = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) scope.launch { status = runTestMic() } else status = "麦克风权限被拒绝"
    }
    // 系统音频授权（MediaProjection）
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
        // ── 标题 ──
        Text("LAN Audio Bridge 手机推流端", style = MaterialTheme.typography.headlineMedium, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(4.dp))
        Text("局域网音频测试面板", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(20.dp))

        // ── 路线选择 ──
        Text("选择测试路线", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(4.dp))
        Column(Modifier.selectableGroup()) {
            (0..3).forEach { r ->
                val capMode = routeToCapture(r)
                Row(
                    modifier = Modifier.fillMaxWidth().height(40.dp).selectable(
                        selected = r == route, onClick = {
                            route = r
                            if (streaming) scope.launch {
                                withContext(Dispatchers.IO) {
                                    val needProj = capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX
                                    val ok = pipe.switchMode(capMode, if (needProj && projReady) proj else null, act)
                                    if (!ok) withContext(Dispatchers.Main) { status = "需先授权系统音频" }
                                    else HandshakeManager.sendRouteUpdate(ip, r)
                                }
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
            Spacer(Modifier.weight(1f))
            if (discoveredDevices.isNotEmpty()) Text("已发现 ${discoveredDevices.size} 台",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
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
                act.stopService(Intent(act, StreamingService::class.java))
                pipe.stopStreaming(); streaming = false
                reconnectionManager.lastKnownHost = null  // 用户手动停止，不清除重连管理器自身
                stateManager.update(ConnectionState.DISCONNECTED); status = "已停止"
            } else {
                if (ContextCompat.checkSelfPermission(act, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
                { micPerm.launch(Manifest.permission.RECORD_AUDIO); return@Button }
                val capMode = routeToCapture(route)
                if ((capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX) && !projReady)
                { status = "请先授权系统音频"; return@Button }
                scope.launch {
                    stateManager.update(ConnectionState.CONNECTING); status = "正在握手..."
                    val handshakeOk = withContext(Dispatchers.IO) { HandshakeManager.handshake(ip, route) }
                    if (!handshakeOk) { stateManager.update(ConnectionState.ERROR); status = "握手失败"; return@launch }
                    stateManager.update(ConnectionState.CONNECTED)
                    val ok = withContext(Dispatchers.IO) {
                        pipe.startStreaming(capMode, if (capMode == AudioPipeline.MODE_SYSTEM || capMode == AudioPipeline.MODE_MIX) proj else null, act, ip)
                    }
                    if (ok) {
                        streaming = true
                        // 保存最后连接信息供重连使用
                        reconnectionManager.lastKnownHost = ip
                        reconnectionManager.lastRouteMode = route
                        pipe.onFirstFrame = { stateManager.update(ConnectionState.STREAMING) }
                        status = "推流中：${routeNames[route]} -> $ip"
                        act.startForegroundService(Intent(act, StreamingService::class.java))
                    } else { stateManager.update(ConnectionState.ERROR); status = "启动推流失败" }
                }
            }
        }, modifier = Modifier.fillMaxWidth().height(48.dp))
        { Text(if (streaming) "停止推流" else "开始推流") }

        // ── 快速测试按钮（内环验证采集→编码，不依赖网络） ──
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            if (ContextCompat.checkSelfPermission(act, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                scope.launch { status = runTestMic() }
            else status = "快速测试需要麦克风权限"
        }, modifier = Modifier.fillMaxWidth().height(40.dp))
        { Text("快速测试麦克风编码（3 秒）") }
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
