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
 * 前台服务 — 推流期间保持后台采集能力
 *
 * - Android 14+: 系统音频采集需要 FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
 * - Android 9+:  后台麦克风采集需要 FOREGROUND_SERVICE_TYPE_MICROPHONE
 *
 * 由 [MainActivity] 在 Start/Stop 推流时启停，不自主管理生命周期
 */
class StreamingService : Service() {

    companion object {
        const val CHANNEL_ID = "streaming_channel"
        const val NOTIFICATION_ID = 1002
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

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "音频推流",
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = "推流期间保持后台运行" }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification {
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("LAN Audio Bridge")
            .setContentText("正在推流中...")
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setOngoing(true)
            .build()
    }
}
