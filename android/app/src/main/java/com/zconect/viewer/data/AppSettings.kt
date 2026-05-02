package com.zconect.viewer.data

import android.content.Context
import android.content.SharedPreferences

/**
 * Centralized app settings backed by SharedPreferences.
 *
 * Sensitive credentials (loginCode, passCode) хранятся в EncryptedSharedPreferences
 * (через [SecurePrefs]) — AES-256-GCM с master key в Android Keystore.
 * Audit 2026-04-27: раньше plain SharedPreferences → ADB pull / Google
 * backup мог вытащить login/pass. Now hardware-backed encryption.
 *
 * Migration: при первом launch'е версии с encrypted storage, plain values
 * автоматически переносятся в encrypted prefs и удаляются из legacy.
 * Idempotent — repeat'ы no-op.
 */
class AppSettings private constructor(
    private val prefs: SharedPreferences,
    private val securePrefs: SecurePrefs,
) {

    companion object {
        private const val PREFS_NAME = "zconect_settings"

        // Keys
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_STUN_URL = "stun_url"
        private const val KEY_TURN_URL = "turn_url"
        private const val KEY_TURN_USER = "turn_user"
        private const val KEY_TURN_PASS = "turn_pass"
        private const val KEY_HOST_SCREEN_W = "host_screen_w"
        private const val KEY_HOST_SCREEN_H = "host_screen_h"
        private const val KEY_LOGIN_CODE = "login_code"
        private const val KEY_PASS_CODE = "pass_code"
        private const val KEY_TRACKPAD_SPEED = "trackpad_speed"
        private const val KEY_SCROLL_SPEED = "scroll_speed"
        private const val KEY_AUTO_RECONNECT = "auto_reconnect"

        // Defaults
        // Production server (HTTPS via Caddy, deployed на connect.zconn.ru).
        // Mirror WPF defaults в client/UiApp/Services/SettingsService.cs.
        // Existing installs auto-migrate в первый run если save'нут "92.63.102.244:8080"
        // (см. migrateLegacyServerUrl ниже).
        const val DEFAULT_SERVER_URL = "https://connect.zconn.ru"
        const val DEFAULT_STUN_URL = "stun:92.63.102.244:3478"
        const val DEFAULT_TURN_URL = "turn:92.63.102.244:3478"
        // Legacy URL для one-shot migration на свежий default. Если saved value
        // совпадает с этим — auto-replace на DEFAULT_SERVER_URL.
        private val LEGACY_SERVER_URLS = setOf(
            "92.63.102.244:8080",
            "http://92.63.102.244:8080",
            "https://92.63.102.244:8080",
        )
        // Audit fix 2026-04-24: пустые defaults для TURN creds. С rotating TURN
        // (RFC 7635) сервер выдаёт per-session creds через session response — static
        // defaults больше не нужны. Оставлять hardcoded "123456" = leak в APK через
        // reverse engineering, любой с декомпиленным APK мог использовать наш coturn
        // как anonymous relay (до перехода на --use-auth-secret).
        const val DEFAULT_TURN_USER = ""
        const val DEFAULT_TURN_PASS = ""
        const val DEFAULT_HOST_SCREEN_W = 3000
        const val DEFAULT_HOST_SCREEN_H = 2000
        const val DEFAULT_TRACKPAD_SPEED = 1.8f
        const val DEFAULT_SCROLL_SPEED = 40f
        const val DEFAULT_AUTO_RECONNECT = true

        @Volatile
        private var instance: AppSettings? = null

        fun getInstance(context: Context): AppSettings {
            return instance ?: synchronized(this) {
                instance ?: run {
                    val plain = context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                    val secure = SecurePrefs.getInstance(context)
                    // One-shot migration: переносим sensitive keys из plain → encrypted
                    // (idempotent — если уже migrated, no-op + cleanup plain).
                    secure.migrateFromPlain(plain, listOf(KEY_LOGIN_CODE, KEY_PASS_CODE))
                    AppSettings(plain, secure).also { instance = it }
                }
            }
        }
    }

    // --- Properties ---

    var serverUrl: String
        get() {
            val saved = prefs.getString(KEY_SERVER_URL, DEFAULT_SERVER_URL) ?: DEFAULT_SERVER_URL
            // Legacy URL auto-migration (mirrors WPF SettingsService.MigrateLegacyUrls
            // commit 866ecf3). Existing installs с DEFAULT_SERVER_URL = "92.63.102.244:8080"
            // получают upgrade на https://connect.zconn.ru без user action.
            if (saved in LEGACY_SERVER_URLS) {
                prefs.edit().putString(KEY_SERVER_URL, DEFAULT_SERVER_URL).apply()
                return DEFAULT_SERVER_URL
            }
            return saved
        }
        set(value) = prefs.edit().putString(KEY_SERVER_URL, value).apply()

    var stunUrl: String
        get() = prefs.getString(KEY_STUN_URL, DEFAULT_STUN_URL) ?: DEFAULT_STUN_URL
        set(value) = prefs.edit().putString(KEY_STUN_URL, value).apply()

    var turnUrl: String
        get() = prefs.getString(KEY_TURN_URL, DEFAULT_TURN_URL) ?: DEFAULT_TURN_URL
        set(value) = prefs.edit().putString(KEY_TURN_URL, value).apply()

    var turnUser: String
        get() = prefs.getString(KEY_TURN_USER, DEFAULT_TURN_USER) ?: DEFAULT_TURN_USER
        set(value) = prefs.edit().putString(KEY_TURN_USER, value).apply()

    var turnPass: String
        get() = prefs.getString(KEY_TURN_PASS, DEFAULT_TURN_PASS) ?: DEFAULT_TURN_PASS
        set(value) = prefs.edit().putString(KEY_TURN_PASS, value).apply()

    var hostScreenW: Int
        get() = prefs.getInt(KEY_HOST_SCREEN_W, DEFAULT_HOST_SCREEN_W)
        set(value) = prefs.edit().putInt(KEY_HOST_SCREEN_W, value).apply()

    var hostScreenH: Int
        get() = prefs.getInt(KEY_HOST_SCREEN_H, DEFAULT_HOST_SCREEN_H)
        set(value) = prefs.edit().putInt(KEY_HOST_SCREEN_H, value).apply()

    // login/passCode — encrypted via SecurePrefs (Audit 2026-04-27 #1).
    // Раньше plain SharedPreferences — readable via ADB pull / Google backup.
    var loginCode: String
        get() = securePrefs.getString(KEY_LOGIN_CODE, "") ?: ""
        set(value) = securePrefs.putString(KEY_LOGIN_CODE, value)

    var passCode: String
        get() = securePrefs.getString(KEY_PASS_CODE, "") ?: ""
        set(value) = securePrefs.putString(KEY_PASS_CODE, value)

    var trackpadSpeed: Float
        get() = prefs.getFloat(KEY_TRACKPAD_SPEED, DEFAULT_TRACKPAD_SPEED)
        set(value) = prefs.edit().putFloat(KEY_TRACKPAD_SPEED, value).apply()

    var scrollSpeed: Float
        get() = prefs.getFloat(KEY_SCROLL_SPEED, DEFAULT_SCROLL_SPEED)
        set(value) = prefs.edit().putFloat(KEY_SCROLL_SPEED, value).apply()

    var autoReconnect: Boolean
        get() = prefs.getBoolean(KEY_AUTO_RECONNECT, DEFAULT_AUTO_RECONNECT)
        set(value) = prefs.edit().putBoolean(KEY_AUTO_RECONNECT, value).apply()

    // --- Convenience ---
    //
    // serverUrl can be entered in one of three forms:
    //   1. "host:port"           → http:// + ws://  (legacy/LAN/IP mode)
    //   2. "http://host:port"    → http:// + ws://  (explicit HTTP)
    //   3. "https://host[:port]" → https:// + wss:// (HTTPS production mode через Caddy)
    //
    // Auto-detection saves user from configuring 3 URLs — just paste full URL
    // or host:port, apps will derive http/ws vs https/wss automatically.

    /** Normalized base URL for HTTP API calls (/healthz, /api/v1/...). */
    val httpBaseUrl: String get() {
        val s = serverUrl.trim().trimEnd('/')
        return when {
            s.startsWith("https://", ignoreCase = true) -> s
            s.startsWith("http://", ignoreCase = true) -> s
            else -> "http://$s"
        }
    }

    /** Normalized base URL for WebSocket connections (/ws). Matches scheme of httpBaseUrl. */
    val wsBaseUrl: String get() {
        val s = serverUrl.trim().trimEnd('/')
        return when {
            s.startsWith("https://", ignoreCase = true) -> "wss://" + s.removePrefix("https://").removePrefix("HTTPS://")
            s.startsWith("http://", ignoreCase = true) -> "ws://" + s.removePrefix("http://").removePrefix("HTTP://")
            else -> "ws://$s"
        }
    }

    /** True если server использует HTTPS — клиенту можно отображать lock icon. */
    val isHttps: Boolean get() = serverUrl.trim().startsWith("https://", ignoreCase = true)

    fun resetToDefaults() {
        serverUrl = DEFAULT_SERVER_URL
        stunUrl = DEFAULT_STUN_URL
        turnUrl = DEFAULT_TURN_URL
        turnUser = DEFAULT_TURN_USER
        turnPass = DEFAULT_TURN_PASS
        hostScreenW = DEFAULT_HOST_SCREEN_W
        hostScreenH = DEFAULT_HOST_SCREEN_H
        trackpadSpeed = DEFAULT_TRACKPAD_SPEED
        scrollSpeed = DEFAULT_SCROLL_SPEED
        autoReconnect = DEFAULT_AUTO_RECONNECT
    }
}
