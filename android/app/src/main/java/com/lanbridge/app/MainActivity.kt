package com.lanbridge.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import com.lanbridge.app.ui.TestUI
import com.lanbridge.app.ui.theme.LanAudioBridgeTheme

/**
 * MainActivity — 启动入口 + 组装
 *
 * 职责仅三件事：
 *   1. 组装 — new 出 UI 组件
 *   2. 注入 — 把 LinkManager 传给 UI（在 TestUI 内部完成）
 *   3. 启动 — setContent 启动界面
 *
 * 不含任何链路逻辑、UI 布局、权限申请代码。
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            LanAudioBridgeTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) { TestUI() }
            }
        }
    }
}
