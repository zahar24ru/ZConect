package com.zconect.viewer.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

@Serializable
data class JoinRequest(
    @SerialName("login_code") val loginCode: String,
    @SerialName("pass_code") val passCode: String
)

@Serializable
data class JoinResponse(
    @SerialName("session_id") val sessionId: String,
    @SerialName("require_confirm") val requireConfirm: Boolean = false,
    val state: String = "",
    @SerialName("ws_url") val wsUrl: String,
    @SerialName("ws_token") val wsToken: String,
    /**
     * Rotating TURN credentials (RFC 7635). Если null — server в legacy mode,
     * клиент fallback'ится на static AppSettings.turnUrl/turnUser/turnPass.
     */
    @SerialName("turn_servers") val turnServers: List<TurnServerConfig>? = null
)

/**
 * TURN server entry из session response'а. Mirror структуры Go TurnServerConfig.
 * @property urls TURN URLs (например "turn:92.63.102.244:3478?transport=udp").
 * @property username HMAC-signed username "<exp_ts>:<session>". Empty для public STUN.
 * @property credential HMAC base64. Empty для public STUN.
 * @property expiresAtUnix когда credential истечёт — клиент должен refresh до этого.
 * @property ttlSeconds = expiresAtUnix - server_time, для client-side refresh scheduling.
 */
@Serializable
data class TurnServerConfig(
    val urls: List<String> = emptyList(),
    val username: String = "",
    val credential: String = "",
    @SerialName("expires_at_unix") val expiresAtUnix: Long = 0,
    @SerialName("ttl_seconds") val ttlSeconds: Int = 0
)

class SessionApi(private val baseUrl: String) {

    private val json = Json { ignoreUnknownKeys = true }

    // Shared OkHttpClient — экономит thread pool + connection pool на rapid
    // navigation events (audit fix 2026-04-27, см. HttpClientProvider).
    private val client get() = HttpClientProvider.api

    /**
     * Детальные reason-специфические exceptions. Mirror Windows JoinSessionStatus enum —
     * позволяют UI показать proper user-facing сообщение вместо generic "connection failed".
     */
    sealed class ApiException(message: String) : Exception(message) {
        /** HTTP 401 — login/pass неверный (generic, unified чтобы не leak'нуть валидные logins). */
        class InvalidCredentials : ApiException("Invalid login or pass code")

        /** HTTP 429 — temporary lock после N wrong attempts. retryAfterSec > 0 — сколько ждать. */
        class SessionLocked(val retryAfterSec: Int) : ApiException(
            if (retryAfterSec > 0) "Session locked. Try in $retryAfterSec sec"
            else "Session temporarily locked. Try later"
        )

        /** HTTP 423 — permanent block после 20+ wrong attempts. Host должен refresh pass. */
        class SessionBlocked : ApiException("Session blocked due to too many failed attempts. Host must refresh password")

        /** HTTP 403 — IP забанен админом. */
        class Banned : ApiException("Your IP is banned on the server. Contact administrator")

        /** HTTP 503 — server в maintenance mode. */
        class Maintenance : ApiException("Server is under maintenance. Try again later")

        /** Timeout / DNS / connection refused. */
        class NetworkError(cause: Throwable) : ApiException("No connection to server: ${cause.message}")

        /** HTTP 5xx или unknown. */
        class ServerError(code: Int) : ApiException("Server error: $code")
    }

    suspend fun joinSession(loginCode: String, passCode: String): JoinResponse =
        withContext(Dispatchers.IO) {
            val body = json.encodeToString(JoinRequest.serializer(), JoinRequest(loginCode, passCode))
            val request = Request.Builder()
                .url("$baseUrl/api/v1/session/join")
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()

            try {
                val response = client.newCall(request).execute()
                when (response.code) {
                    200 -> {
                        val responseBody = response.body?.string()
                            ?: throw ApiException.ServerError(200)
                        json.decodeFromString(JoinResponse.serializer(), responseBody)
                    }
                    401 -> throw ApiException.InvalidCredentials()
                    403 -> throw ApiException.Banned()
                    423 -> throw ApiException.SessionBlocked()
                    429 -> {
                        // Retry-After header (RFC 7231 standard) — fallback на retry_after_sec в body.
                        val headerVal = response.header("Retry-After")?.toIntOrNull() ?: 0
                        val retryAfter = if (headerVal > 0) headerVal else {
                            try {
                                val errBody = response.body?.string().orEmpty()
                                val obj = json.parseToJsonElement(errBody).let {
                                    if (it is kotlinx.serialization.json.JsonObject) it else null
                                }
                                obj?.get("retry_after_sec")?.toString()?.toIntOrNull() ?: 0
                            } catch (_: Exception) { 0 }
                        }
                        throw ApiException.SessionLocked(retryAfter)
                    }
                    503 -> throw ApiException.Maintenance()
                    in 500..599 -> throw ApiException.ServerError(response.code)
                    else -> throw ApiException.ServerError(response.code)
                }
            } catch (e: ApiException) {
                throw e
            } catch (e: Exception) {
                throw ApiException.NetworkError(e)
            }
        }
}
