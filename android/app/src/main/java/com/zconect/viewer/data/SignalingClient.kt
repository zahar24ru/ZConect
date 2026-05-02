package com.zconect.viewer.data

import android.util.Log
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.*
import okhttp3.*
import java.util.concurrent.TimeUnit

/**
 * Signaling message envelope matching the server protocol.
 * { "type": "offer|answer|ice|auth|peer_state", "session_id": "...", "payload": {...} }
 */
@Serializable
data class SignalingMessage(
    val type: String,
    @kotlinx.serialization.SerialName("session_id")
    val sessionId: String = "",
    val payload: JsonObject = JsonObject(emptyMap())
)

/**
 * WebSocket signaling client for ZConect.
 * Handles auth, peer_state, and relays offer/answer/ice messages.
 */
class SignalingClient {

    companion object {
        private const val TAG = "SignalingClient"
    }

    private val json = Json { ignoreUnknownKeys = true }
    private var webSocket: WebSocket? = null

    private val _messages = MutableSharedFlow<SignalingMessage>(extraBufferCapacity = 64)
    val messages: SharedFlow<SignalingMessage> = _messages

    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected

    private val _disconnectReason = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val disconnectReason: SharedFlow<String> = _disconnectReason

    private var currentSessionId: String = ""

    // Shared WebSocket client — same connection pool как REST API,
    // но с no read timeout + pingInterval=20s (audit fix 2026-04-27).
    private val client get() = HttpClientProvider.websocket

    fun connect(wsUrl: String, sessionId: String, token: String) {
        currentSessionId = sessionId
        // URL-encode sessionId — defensive против спецсимволов (=, &, #, пробелы).
        // Сейчас sessionIDs UUID-формата (alphanumeric + dashes), не нужно encoding,
        // но если server format когда-нибудь поменяется — bug бы тихо ломал WS connect.
        val encodedSessionId = java.net.URLEncoder.encode(sessionId, "UTF-8")
        val url = "$wsUrl?session_id=$encodedSessionId"
        Log.d(TAG, "Connecting to WebSocket (session=$sessionId)")

        val request = Request.Builder().url(url).build()
        webSocket = client.newWebSocket(request, object : WebSocketListener() {

            override fun onOpen(webSocket: WebSocket, response: Response) {
                Log.d(TAG, "WebSocket opened")
                _connected.value = true

                // Step 1: Send auth message
                val authMsg = buildJsonObject {
                    put("type", "auth")
                    put("session_id", sessionId)
                    put("payload", buildJsonObject {
                        put("token", token)
                    })
                }
                webSocket.send(authMsg.toString())
                Log.d(TAG, "Sent auth")

                // Step 2: Send peer_state "joined"
                val peerStateMsg = buildJsonObject {
                    put("type", "peer_state")
                    put("session_id", sessionId)
                    put("payload", buildJsonObject {
                        put("state", "joined")
                    })
                }
                webSocket.send(peerStateMsg.toString())
                Log.d(TAG, "Sent peer_state joined")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                try {
                    val msg = json.decodeFromString(SignalingMessage.serializer(), text)
                    Log.d(TAG, "Received: type=${msg.type}")
                    _messages.tryEmit(msg)
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to parse message: $text", e)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                Log.d(TAG, "WebSocket closing: $code $reason")
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                Log.d(TAG, "WebSocket closed: $code $reason")
                _connected.value = false
                _disconnectReason.tryEmit(reason.ifEmpty { "Connection closed" })
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.e(TAG, "WebSocket failure: ${t.message}", t)
                _connected.value = false
                _disconnectReason.tryEmit(t.message ?: "Connection failed")
            }
        })
    }

    fun send(type: String, payload: JsonObject = JsonObject(emptyMap())) {
        val msg = buildJsonObject {
            put("type", type)
            put("session_id", currentSessionId)
            put("payload", payload)
        }
        val text = msg.toString()
        val sent = webSocket?.send(text) ?: false
        if (!sent) {
            Log.w(TAG, "Failed to send message: type=$type")
        }
    }

    fun sendOffer(sdp: String) {
        send("offer", buildJsonObject { put("sdp", sdp) })
    }

    fun sendAnswer(sdp: String) {
        send("answer", buildJsonObject { put("sdp", sdp) })
    }

    fun sendIceCandidate(candidate: String, sdpMid: String, sdpMLineIndex: Int) {
        send("ice", buildJsonObject {
            put("candidate", candidate)
            put("sdpMid", sdpMid)
            put("sdpMLineIndex", sdpMLineIndex)
        })
    }

    fun disconnect() {
        webSocket?.close(1000, "Viewer disconnecting")
        webSocket = null
        _connected.value = false
    }
}
