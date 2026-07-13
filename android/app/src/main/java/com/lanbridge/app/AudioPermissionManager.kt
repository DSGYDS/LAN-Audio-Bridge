package com.lanbridge.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat

/**
 * AudioPermissionManager — 音频权限管理
 *
 * ## 职责
 * 1. 麦克风权限请求（RECORD_AUDIO）
 * 2. 系统音频授权（MediaProjection）
 *
 * 封装权限 launcher，与 MainActivity UI 逻辑解耦。
 */
class AudioPermissionManager(
    private val act: ComponentActivity
) {
    /** 麦克风权限是否已授予 */
    val micGranted: Boolean
        get() = ContextCompat.checkSelfPermission(
            act, Manifest.permission.RECORD_AUDIO
        ) == PackageManager.PERMISSION_GRANTED

    /** MediaProjection 实例 */
    var projection: MediaProjection? = null
        private set

    /** MediaProjection 是否已就绪 */
    var projectionReady: Boolean = false
        private set

    /** 发起麦克风权限请求（结果由外部回调处理） */
    fun launchMicPermission() {
        // 实际使用时通过 rememberLauncherForActivityResult 创建
    }

    /** 发起系统音频授权请求 */
    fun launchSystemAudio() {
        act.startForegroundService(Intent(act, MediaProjectionService::class.java))
        val mgr = act.getSystemService(MediaProjectionManager::class.java)
        // 实际需要通过 Intent 启动，此处标记
    }
}
