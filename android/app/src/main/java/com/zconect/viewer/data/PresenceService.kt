package com.zconect.viewer.data

import android.util.Log
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/** Состояние presence для одного login_code. */
enum class PresenceState { ONLINE, OFFLINE, UNKNOWN }

/**
 * Poll'ит server /api/v1/presence?logins=X,Y,Z каждые 30 сек чтобы показать
 * 🟢/🔴/⚪ dot у saved contacts и recent cards.
 *
 * Mirror Windows PresenceService (Services/PresenceService.cs).
 *
 * "Online" = у login_code есть active non-expired session на сервере
 * → host готов принять viewer'а.
 *
 * Использование:
 *   val service = PresenceService(context)
 *   service.setLogins(contacts.map { it.loginCode })
 *   // observe service.presenceMap.collectAsState()
 *   service.state(loginCode)  // returns PresenceState
 *   service.dispose()  // when screen closed
 */
class PresenceService(
    private val serverBaseUrl: String,
    private val pollIntervalMs: Long = 30_000L,
) {
    // Shared client — fast-fail timeouts (5s/8s) для presence polling
    // (audit fix 2026-04-27).
    private val client get() = HttpClientProvider.telemetry

    private val json = Json { ignoreUnknownKeys = true }

    private val _presenceMap = MutableStateFlow<Map<String, PresenceState>>(emptyMap())
    val presenceMap: StateFlow<Map<String, PresenceState>> = _presenceMap.asStateFlow()

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var pollJob: Job? = null
    private var currentLogins: List<String> = emptyList()

    fun state(loginCode: String): PresenceState =
        _presenceMap.value[loginCode] ?: PresenceState.UNKNOWN

    /**
     * Устанавливает список login_codes для мониторинга. Если пустой — останавливает
     * polling. Повторные вызовы с тем же списком — no-op.
     */
    fun setLogins(logins: List<String>) {
        val filtered = logins.filter { it.length == 8 }.distinct().take(100)
        if (filtered == currentLogins) return
        currentLogins = filtered

        pollJob?.cancel()
        if (filtered.isEmpty()) {
            _presenceMap.value = emptyMap()
            return
        }

        pollJob = scope.launch {
            while (isActive) {
                pollOnce(filtered)
                delay(pollIntervalMs)
            }
        }
    }

    /** Force immediate refresh (e.g. pull-to-refresh). */
    suspend fun refresh() {
        if (currentLogins.isNotEmpty()) pollOnce(currentLogins)
    }

    @Serializable
    private data class PresenceResponse(val online: Map<String, Boolean>)

    private suspend fun pollOnce(logins: List<String>) {
        try {
            val url = "${serverBaseUrl.trimEnd('/')}/api/v1/presence?logins=${logins.joinToString(",")}"
            val req = Request.Builder().url(url).get().build()
            val resp = withContext(Dispatchers.IO) { client.newCall(req).execute() }
            resp.use { r ->
                if (!r.isSuccessful) {
                    Log.d(TAG, "presence http=${r.code}")
                    return
                }
                val body = r.body?.string() ?: return
                val parsed = json.decodeFromString(PresenceResponse.serializer(), body)
                val newMap = mutableMapOf<String, PresenceState>()
                for (login in logins) {
                    val online = parsed.online[login]
                    newMap[login] = when (online) {
                        true -> PresenceState.ONLINE
                        false -> PresenceState.OFFLINE
                        null -> PresenceState.UNKNOWN
                    }
                }
                _presenceMap.value = newMap
            }
        } catch (e: Exception) {
            // Network error — не менять state, следующий poll повторит.
            // Unknown remains unknown для привилегии показа "?" dot over false offline.
            Log.d(TAG, "presence_poll_error: ${e.message}")
        }
    }

    fun dispose() {
        pollJob?.cancel()
        scope.cancel()
    }

    companion object {
        private const val TAG = "PresenceService"
    }
}
