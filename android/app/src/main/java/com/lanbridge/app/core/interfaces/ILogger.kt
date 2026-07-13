package com.lanbridge.app.core.interfaces

interface ILogger {
    fun debug(tag: String, msg: String)
    fun info(tag: String, msg: String)
    fun warn(tag: String, msg: String)
    fun error(tag: String, msg: String)
    fun error(tag: String, msg: String, ex: Exception)
}
