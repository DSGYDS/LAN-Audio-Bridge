package com.lanbridge.app.core.impl

import android.util.Log
import com.lanbridge.app.core.interfaces.ILogger

class LogcatLogger : ILogger {
    override fun debug(tag: String, msg: String) { Log.d(tag, msg) }
    override fun info(tag: String, msg: String) { Log.i(tag, msg) }
    override fun warn(tag: String, msg: String) { Log.w(tag, msg) }
    override fun error(tag: String, msg: String) { Log.e(tag, msg) }
    override fun error(tag: String, msg: String, ex: Exception) {
        Log.e(tag, "$msg: ${ex.message}")
    }
}
