package com.zconect.viewer.data

import android.content.Context
import android.os.Build
import android.util.Log
import kotlinx.coroutines.*
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.Locale
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * Sends periodic anonymous heartbeat to the signaling server.
 * Collects: machine_id (random UUID stored locally), platform, app version,
 * OS version, UI language, device model. GeoIP country resolved server-side.
 *
 * Privacy: NO personal data, NO IMEI/MAC/phone number, NO session credentials.
 * Errors are silently swallowed — telemetry MUST NOT break the app.
 */
class TelemetryService(private val context: Context) {

    companion object {
        private const val TAG = "Telemetry"
        private const val PREFS = "zconect_telemetry"
        private const val KEY_MACHINE_ID = "machine_id"
        private const val HEARTBEAT_INTERVAL_MIN = 5L // every 5 minutes
        private const val APP_VERSION = com.zconect.viewer.BuildConfig.VERSION_NAME
    }

    @Serializable
    private data class HeartbeatPayload(
        @SerialName("machine_id") val machineId: String,
        @SerialName("app_version") val appVersion: String,
        @SerialName("os_version") val osVersion: String,
        @SerialName("os_language") val osLanguage: String,
        val platform: String = "android",
        @SerialName("device_model") val deviceModel: String
    )

    private val json = Json { encodeDefaults = true; ignoreUnknownKeys = true }
    // Shared client (audit fix 2026-04-27).
    private val httpClient get() = HttpClientProvider.telemetry

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null

    /** Get or generate a persistent random machine_id. */
    private val machineId: String by lazy {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs.getString(KEY_MACHINE_ID, null) ?: run {
            val fresh = UUID.randomUUID().toString()
            prefs.edit().putString(KEY_MACHINE_ID, fresh).apply()
            fresh
        }
    }

    private val deviceModel: String by lazy {
        val m = Build.MANUFACTURER ?: ""
        val model = Build.MODEL ?: ""
        when {
            model.isBlank() -> m
            m.isBlank() -> model
            model.startsWith(m, ignoreCase = true) -> model
            else -> "$m $model"
        }.trim()
    }

    private val osVersion: String by lazy { "Android ${Build.VERSION.RELEASE}" }

    private val osLanguage: String by lazy {
        val locale = Locale.getDefault()
        val tag = locale.toLanguageTag()
        if (tag.isNullOrBlank() || tag == "und") "${locale.language}-${locale.country}" else tag
    }

    /** Start periodic heartbeat loop. Safe to call multiple times — only one loop runs. */
    fun start() {
        if (job?.isActive == true) return
        job = scope.launch {
            // First heartbeat right away
            sendHeartbeat()
            while (isActive) {
                delay(TimeUnit.MINUTES.toMillis(HEARTBEAT_INTERVAL_MIN))
                sendHeartbeat()
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
    }

    private suspend fun sendHeartbeat() {
        try {
            val settings = AppSettings.getInstance(context)
            val baseUrl = settings.httpBaseUrl // "http://<server>"
            val payload = HeartbeatPayload(
                machineId = machineId,
                appVersion = APP_VERSION,
                osVersion = osVersion,
                osLanguage = osLanguage,
                deviceModel = deviceModel
            )
            val body = json.encodeToString(HeartbeatPayload.serializer(), payload)
            val request = Request.Builder()
                .url("$baseUrl/api/v1/telemetry/heartbeat")
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()
            httpClient.newCall(request).execute().use { resp ->
                Log.d(TAG, "heartbeat response=${resp.code}")
            }
        } catch (e: Exception) {
            Log.w(TAG, "heartbeat failed: ${e.message}")
        }
    }
}
