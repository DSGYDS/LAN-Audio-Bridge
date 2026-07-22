package com.lanbridge.app.ui

import android.annotation.SuppressLint
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import com.lanbridge.app.core.infrastructure.Log
import java.util.concurrent.Executors

/**
 * QR 码解析结果
 */
data class LabridgeQrCode(
    val transport: String,
    val deviceName: String,
    val token: String,
    val ssid: String = "",      // P2P 网络 SSID（Legacy 模式，新版不需要）
    val pass: String = "",      // P2P 网络密码（新版不需要）
    val host: String = ""       // Windows P2P IP（新版不需要）
)

/**
 * 解析 LABRIDGE:// QR 码内容
 *
 * 格式：LABRIDGE://version=1&transport=wifidirect&device=XXX&token=XXX&ssid=XXX&pass=XXX&host=XXX
 */
fun parseLabridgeQr(content: String): LabridgeQrCode? {
    if (!content.startsWith("LABRIDGE://")) return null
    val params = content.removePrefix("LABRIDGE://")
        .split("&")
        .associate {
            val kv = it.split("=", limit = 2)
            kv[0] to (kv.getOrNull(1) ?: "")
        }
    return LabridgeQrCode(
        transport = params["transport"] ?: return null,
        deviceName = params["device"] ?: return null,
        token = params["token"] ?: "",
        ssid = params["ssid"] ?: "",
        pass = params["pass"] ?: "",
        host = params["host"] ?: ""
    )
}

/**
 * QR 扫码界面 — CameraX 预览 + ML Kit 实时条码识别
 *
 * @param onScanned 扫码成功回调（返回解析后的 QR 数据）
 * @param onDismiss 关闭扫码界面
 */
@Composable
fun QrScannerScreen(
    onScanned: (LabridgeQrCode) -> Unit,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var hasScanned by remember { mutableStateOf(false) }
    var hintText by remember { mutableStateOf("将 QR 码对准摄像头") }

    val scanner = remember {
        val options = BarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .build()
        BarcodeScanning.getClient(options)
    }

    val analysisExecutor = remember { Executors.newSingleThreadExecutor() }

    DisposableEffect(Unit) {
        onDispose {
            analysisExecutor.shutdown()
            scanner.close()
        }
    }

    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        // 提示文字
        Text(
            text = hintText,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(16.dp)
        )

        // 相机预览
        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            AndroidView(
                factory = { ctx ->
                    PreviewView(ctx).apply {
                        scaleType = PreviewView.ScaleType.FILL_CENTER
                    }
                },
                modifier = Modifier.fillMaxSize(),
                update = { previewView ->
                    if (hasScanned) return@AndroidView

                    val cameraProviderFuture = ProcessCameraProvider.getInstance(context)
                    cameraProviderFuture.addListener({
                        val cameraProvider = cameraProviderFuture.get()

                        // Preview
                        val preview = Preview.Builder().build().also {
                            it.surfaceProvider = previewView.surfaceProvider
                        }

                        // ImageAnalysis → ML Kit
                        val imageAnalysis = ImageAnalysis.Builder()
                            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                            .build()

                        imageAnalysis.setAnalyzer(analysisExecutor) { imageProxy ->
                            processFrame(imageProxy, scanner) { result ->
                                if (!hasScanned && result != null) {
                                    hasScanned = true
                                    val parsed = parseLabridgeQr(result)
                                    if (parsed != null) {
                                        onScanned(parsed)
                                    } else {
                                        hasScanned = false
                                        hintText = "非 LAN Audio Bridge 二维码，请重新扫描"
                                    }
                                }
                            }
                        }

                        // 绑定
                        val cameraSelector = CameraSelector.DEFAULT_BACK_CAMERA
                        try {
                            cameraProvider.unbindAll()
                            cameraProvider.bindToLifecycle(
                                lifecycleOwner, cameraSelector, preview, imageAnalysis
                            )
                        } catch (e: Exception) {
                            Log.e("QrScanner", "Camera bind error: ${e.message}")
                        }
                    }, ContextCompat.getMainExecutor(context))
                }
            )
        }

        // 取消按钮
        Button(
            onClick = onDismiss,
            modifier = Modifier.fillMaxWidth().padding(16.dp).height(48.dp)
        ) {
            Text("取消扫码")
        }
    }
}

/**
 * 处理相机帧 → ML Kit 条码识别
 */
@SuppressLint("UnsafeOptInUsageError")
private fun processFrame(
    imageProxy: ImageProxy,
    scanner: com.google.mlkit.vision.barcode.BarcodeScanner,
    onResult: (String?) -> Unit
) {
    val mediaImage = imageProxy.image
    if (mediaImage == null) {
        imageProxy.close()
        return
    }

    val inputImage = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)

    scanner.process(inputImage)
        .addOnSuccessListener { barcodes ->
            val qrContent = barcodes.firstOrNull()?.rawValue
            onResult(qrContent)
        }
        .addOnFailureListener {
            onResult(null)
        }
        .addOnCompleteListener {
            imageProxy.close()
        }
}
