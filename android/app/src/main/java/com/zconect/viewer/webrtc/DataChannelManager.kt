package com.zconect.viewer.webrtc

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.*
import org.webrtc.DataChannel
import java.nio.ByteBuffer
import java.nio.charset.Charset

// --- Data channel payload models (camelCase, matching C# DataChannelModels.cs) ---

@Serializable
data class ScreenMeta(
    val captureX: Int = 0,
    val captureY: Int = 0,
    val width: Int = 0,      // 0 = use fallback from AppSettings
    val height: Int = 0,     // 0 = use fallback from AppSettings
    val displayId: String = ""
)

@Serializable
data class CursorShape(
    val cursorType: String = "arrow"
)

@Serializable
data class MouseInput(
    val action: String,    // "move", "down", "up", "wheel"
    val x: Int,
    val y: Int,
    val button: Int = 0,   // 1=left, 2=right, 3=middle
    val delta: Int = 0     // scroll wheel delta
)

@Serializable
data class KeyboardInput(
    val action: String,       // "down", "up"
    val virtualKey: Int,      // Windows VK code
    val scanCode: Int = 0,
    val alt: Boolean = false,
    val ctrl: Boolean = false,
    val shift: Boolean = false,
    val win: Boolean = false
)

@Serializable
data class HostDisplayInfo(
    val id: String = "",
    val name: String = "",
    val isPrimary: Boolean = false
)

@Serializable
data class HostDisplays(
    val displays: List<HostDisplayInfo> = emptyList()
)

@Serializable
data class PingPayload(val timestampMs: Long)

@Serializable
data class HostVideoSettingsRequest(
    val qualityPreset: String = "Auto",
    val displayMode: String = "Current",
    val displayId: String = "",
    val quickReconnect: Boolean = false
)

@Serializable
data class HostDisplaysRequest(val requestId: String)

/**
 * Manages sending/receiving JSON messages over WebRTC data channels.
 * Envelope format: { "type": "snake_case_type", "payload": { camelCaseFields } }
 */
class DataChannelManager {

    companion object {
        private const val TAG = "DataChannelMgr"
    }

    private val json = Json { ignoreUnknownKeys = true }
    private val sendChannels = mutableMapOf<String, DataChannel>()   // locally created — for sending TO host
    private val receiveChannels = mutableMapOf<String, DataChannel>() // from onDataChannel — for receiving FROM host

    // Incoming state from host
    private val _screenMeta = MutableStateFlow(ScreenMeta())
    val screenMeta: StateFlow<ScreenMeta> = _screenMeta

    private val _cursorShape = MutableStateFlow("arrow")
    val cursorShape: StateFlow<String> = _cursorShape

    private val _hostDisplays = MutableStateFlow<List<HostDisplayInfo>>(emptyList())
    val hostDisplays: StateFlow<List<HostDisplayInfo>> = _hostDisplays

    private val _controlChannelOpen = MutableStateFlow(false)
    val controlChannelOpen: StateFlow<Boolean> = _controlChannelOpen

    private val _rttMs = MutableStateFlow(-1)
    val rttMs: StateFlow<Int> = _rttMs

    var onChannelOpen: (() -> Unit)? = null

    /** Attach a locally-created channel (for SENDING to host) */
    fun attachChannel(label: String, channel: DataChannel) {
        sendChannels[label] = channel
        channel.registerObserver(object : DataChannel.Observer {
            override fun onBufferedAmountChange(previousAmount: Long) {}

            override fun onStateChange() {
                val state = channel.state()
                Log.d(TAG, "Send channel '$label' state: $state")
                if (state == DataChannel.State.OPEN) {
                    if (label == "dc-control") _controlChannelOpen.value = true
                    onChannelOpen?.invoke()
                }
            }

            override fun onMessage(buffer: DataChannel.Buffer) {
                val data = ByteArray(buffer.data.remaining())
                buffer.data.get(data)
                // Host sends JSON as binary — always try to parse as UTF-8 text.
                // For file-channel binary chunks, skip (first byte = 0x01 marker).
                if (label == "dc-file" && data.isNotEmpty() && data[0] == 0x01.toByte()) return
                val text = String(data, Charset.forName("UTF-8"))
                handleMessage(label, text)
            }
        })
    }

    /** Attach a remote channel from onDataChannel (for RECEIVING from host) */
    fun attachReceiveChannel(label: String, channel: DataChannel) {
        receiveChannels[label] = channel
        channel.registerObserver(object : DataChannel.Observer {
            override fun onBufferedAmountChange(previousAmount: Long) {}

            override fun onStateChange() {
                val state = channel.state()
                Log.d(TAG, "Recv channel '$label' state: $state")
                if (state == DataChannel.State.OPEN) {
                    onChannelOpen?.invoke()
                }
            }

            override fun onMessage(buffer: DataChannel.Buffer) {
                val data = ByteArray(buffer.data.remaining())
                buffer.data.get(data)
                if (label == "dc-file" && data.isNotEmpty() && data[0] == 0x01.toByte()) return
                val text = String(data, Charset.forName("UTF-8"))
                handleMessage(label, text)
            }
        })
    }

    private fun handleMessage(channel: String, text: String) {
        try {
            Log.d(TAG, "DC message on '$channel': ${text.take(200)}")
            val envelope = json.parseToJsonElement(text).jsonObject
            val type = envelope["type"]?.jsonPrimitive?.content ?: return
            val payload = envelope["payload"]?.jsonObject ?: JsonObject(emptyMap())

            when (type) {
                "screen_meta" -> {
                    val meta = json.decodeFromJsonElement(ScreenMeta.serializer(), payload)
                    _screenMeta.value = meta
                    Log.d(TAG, "screen_meta: ${meta.width}x${meta.height} at (${meta.captureX},${meta.captureY})")
                }
                "cursor_shape" -> {
                    val shape = json.decodeFromJsonElement(CursorShape.serializer(), payload)
                    _cursorShape.value = shape.cursorType
                }
                "host_displays" -> {
                    val displays = json.decodeFromJsonElement(HostDisplays.serializer(), payload)
                    _hostDisplays.value = displays.displays
                    Log.d(TAG, "host_displays: ${displays.displays.size} displays")
                }
                "ping" -> {
                    val ping = json.decodeFromJsonElement(PingPayload.serializer(), payload)
                    sendPong(ping.timestampMs)
                }
                "pong" -> {
                    val pong = json.decodeFromJsonElement(PingPayload.serializer(), payload)
                    val rtt = (System.currentTimeMillis() - pong.timestampMs).toInt()
                    if (rtt in 0..10_000) {
                        _rttMs.value = rtt
                    }
                }
                else -> {
                    Log.v(TAG, "Unhandled message type: $type on channel $channel")
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to parse DC message on $channel: ${text.take(200)}", e)
        }
    }

    // --- Send methods ---

    fun sendMouseInput(input: MouseInput) {
        val payload = json.encodeToJsonElement(MouseInput.serializer(), input).jsonObject
        sendOnChannel("dc-input", "mouse_input", payload)
    }

    fun sendKeyboardInput(input: KeyboardInput) {
        val payload = json.encodeToJsonElement(KeyboardInput.serializer(), input).jsonObject
        sendOnChannel("dc-input", "keyboard_input", payload)
    }

    fun sendPong(timestampMs: Long) {
        val payload = buildJsonObject { put("timestampMs", timestampMs) }
        sendOnChannel("dc-control", "pong", payload)
    }

    fun sendPing() {
        val payload = buildJsonObject { put("timestampMs", System.currentTimeMillis()) }
        sendOnChannel("dc-control", "ping", payload)
    }

    fun sendHostVideoSettingsRequest(request: HostVideoSettingsRequest = HostVideoSettingsRequest()) {
        val payload = json.encodeToJsonElement(HostVideoSettingsRequest.serializer(), request).jsonObject
        sendOnChannel("dc-control", "host_video_settings_request", payload)
    }

    fun sendHostDisplaysRequest(requestId: String) {
        val payload = buildJsonObject { put("requestId", requestId) }
        sendOnChannel("dc-control", "host_displays_request", payload)
    }

    fun sendCtrlAltDel() {
        sendOnChannel("dc-control", "ctrl_alt_del", JsonObject(emptyMap()))
    }

    fun hasChannel(label: String): Boolean = sendChannels.containsKey(label)

    private fun sendOnChannel(channelLabel: String, type: String, payload: JsonObject) {
        // Send on BOTH channels (locally-created AND host-created) to maximize delivery.
        // One of them is the actual SCTP stream the host listens on.
        val envelope = buildJsonObject {
            put("type", type)
            put("payload", payload)
        }
        val text = envelope.toString()
        val bytes = text.toByteArray(Charset.forName("UTF-8"))

        var anySent = false
        listOf(sendChannels[channelLabel], receiveChannels[channelLabel]).forEach { channel ->
            if (channel != null && channel.state() == DataChannel.State.OPEN) {
                val buffer = DataChannel.Buffer(ByteBuffer.wrap(bytes.copyOf()), false)
                val sent = channel.send(buffer)
                if (sent) anySent = true
            }
        }
        Log.d(TAG, "SEND on '$channelLabel' type='$type' sent=$anySent bytes=${bytes.size}")
    }
}
