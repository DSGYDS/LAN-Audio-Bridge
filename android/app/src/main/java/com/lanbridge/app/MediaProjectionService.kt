package com.lanbridge.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

/**
 * 前台服务 — 用于满足 MediaProjection 前台服务类型要求
 *
 * Android 14+ 要求先启动带 FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION 的前台服务，
 * 才能获取 MediaProjection 授权。授权完成后即可停止，
 * 实际推流中的前台由 [StreamingService] 维持。
 */
class MediaProjectionService : Service() {

    companion object {
        const val CHANNEL_ID = "media_projection_channel"
        const val NOTIFICATION_ID = 1001
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = buildNotification()
        startForeground(NOTIFICATION_ID, notification)
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // ── 通知渠道（Android 8+ 必须） ──
    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "音频桥接",
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = "用于 MediaProjection 系统音频采集" }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification {
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("LAN Audio Bridge")
            .setContentText("准备系统音频采集...")
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setOngoing(false)
            .build()
    }
}
