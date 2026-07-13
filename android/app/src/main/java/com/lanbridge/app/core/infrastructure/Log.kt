package com.lanbridge.app.core.infrastructure

import com.lanbridge.app.core.impl.LogcatLogger
import com.lanbridge.app.core.interfaces.ILogger

object Log {
    private var _impl: ILogger = LogcatLogger()

    fun setImpl(impl: ILogger) { _impl = impl }

    fun d(tag: String, msg: String) = _impl.debug(tag, msg)
    fun i(tag: String, msg: String) = _impl.info(tag, msg)
    fun w(tag: String, msg: String) = _impl.warn(tag, msg)
    fun e(tag: String, msg: String) = _impl.error(tag, msg)
    fun e(tag: String, msg: String, ex: Exception) = _impl.error(tag, msg, ex)
}
