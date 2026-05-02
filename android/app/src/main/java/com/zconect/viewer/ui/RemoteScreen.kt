package com.zconect.viewer.ui

import android.app.Activity
import android.content.pm.ActivityInfo
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.util.Log
import android.view.WindowManager
import androidx.compose.animation.*
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.activity.compose.BackHandler
import com.zconect.viewer.input.KeyCaptureView
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.FitScreen
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.filled.ZoomIn
import androidx.compose.material.icons.filled.ZoomOut
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Monitor
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import android.view.inputmethod.InputMethodManager
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import kotlin.math.min
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.zconect.viewer.data.SignalingClient
import com.zconect.viewer.data.SignalingMessage
import com.zconect.viewer.input.TouchInputMapper
import com.zconect.viewer.webrtc.PeerConnectionManager
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.jsonPrimitive
import org.webrtc.RendererCommon
import org.webrtc.SurfaceViewRenderer

private const val TAG = "RemoteScreen"

@Composable
fun RemoteScreen(
    sessionId: String,
    wsUrl: String,
    wsToken: String,
    /**
     * Rotating TURN credentials (RFC 7635). Если null — используется static config из AppSettings.
     * Обновляется при каждом reconnect (joinSession response) — см. reconnect branch ниже.
     */
    initialTurnServers: List<com.zconect.viewer.data.TurnServerConfig>? = null,
    /**
     * Contact ID для saved-password fast-path (null = ad-hoc connect через manual
     * login codes — нет ассоциированного contact'а). Audit fix #4 2026-04-27:
     * silent reconnect к saved contact через сохранённый password.
     */
    contactId: String? = null,
    onDisconnected: () -> Unit
) {
    val context = LocalContext.current
    val activity = context as? Activity

    // Force landscape + keep screen on + immersive fullscreen
    DisposableEffect(Unit) {
        // User feedback 2026-04-27: «оставить на выбор пусть изначально всё
        // работает вертикально а если перевернул то переворачивается в
        // горизонтальный».
        // Раньше force'или SCREEN_ORIENTATION_SENSOR_LANDSCAPE — app
        // переворачивался в landscape сразу при connect, без выбора.
        // Теперь SCREEN_ORIENTATION_USER — respects user's rotation lock:
        // - Rotation lock OFF → auto-rotate по sensor (portrait ⇄ landscape)
        // - Rotation lock ON → остаётся в текущей orientation
        // Если phone в portrait при connect — app тоже в portrait. Перевернул
        // в landscape — auto-rotates.
        activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_USER
        activity?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        onDispose {
            activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    // Settings
    val settings = remember { com.zconect.viewer.data.AppSettings.getInstance(context) }

    // --- Connection objects (remember across recompositions) ---
    val signalingClient = remember { SignalingClient() }
    val peerConnectionManager = remember { PeerConnectionManager(context.applicationContext) }
    val eglBase = remember { peerConnectionManager.getEglBase() }

    // State
    val remoteVideoTrack by peerConnectionManager.remoteVideoTrack.collectAsStateWithLifecycle()
    var statusText by remember { mutableStateOf("Connecting...") }
    var showToolbar by remember { mutableStateOf(true) }
    var showKeyboard by remember { mutableStateOf(false) }
    var isConnected by remember { mutableStateOf(false) }
    var videoWidth by remember { mutableStateOf(1920) }
    var videoHeight by remember { mutableStateOf(1080) }
    var cursorX by remember { mutableStateOf(-1f) }
    var cursorY by remember { mutableStateOf(-1f) }
    var cursorVisible by remember { mutableStateOf(false) }
    var useAspectFill by remember { mutableStateOf(true) }
    var useTrackpad by remember { mutableStateOf(true) }
    var toolbarPinned by remember { mutableStateOf(false) }
    var qualityPreset by remember { mutableStateOf("Auto") }
    var selectedDisplayId by remember { mutableStateOf("") } // "" = current display
    var showQualityMenu by remember { mutableStateOf(false) }
    var showMonitorMenu by remember { mutableStateOf(false) }
    var showHelpDialog by remember { mutableStateOf(false) }
    var showStatsOverlay by remember { mutableStateOf(false) }

    // Pinch zoom state
    var zoomScale by remember { mutableStateOf(1f) }
    var zoomOffsetX by remember { mutableStateOf(0f) }
    var zoomOffsetY by remember { mutableStateOf(0f) }
    var framesReceivedCount by remember { mutableStateOf(0) }
    var fps by remember { mutableStateOf(0) }
    var reconnectAttempt by remember { mutableStateOf(0) }

    // Unattended auth state — prompt to user when host requires password.
    // Pair: (Prompt info, callback that user completes когда entering password
    // OR cancelling). Coroutine inside coordinator awaits callback via
    // CompletableDeferred. Callback receives PromptResult (password + remember flag).
    var unattendedPromptState by remember {
        mutableStateOf<Pair<com.zconect.viewer.data.UnattendedAuthCoordinator.Prompt,
                            (com.zconect.viewer.data.UnattendedAuthCoordinator.PromptResult) -> Unit>?>(null)
    }
    // KeyCaptureView ref — для focus control + keyboard show/hide.
    // Установится в AndroidView factory.
    val keyCaptureViewRef = remember { java.util.concurrent.atomic.AtomicReference<KeyCaptureView?>(null) }
    var keyboardTrigger by remember { mutableStateOf(0) } // increment to toggle
    var isSystemKeyboardVisible by remember { mutableStateOf(false) }

    // Back button handler — при нажатии на hardware back / X button
    // disconnect'им а НЕ закрываем app entirely.
    BackHandler(enabled = true) {
        onDisconnected()
    }

    // Haptic feedback
    val vibrator = remember {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val mgr = context.getSystemService(VibratorManager::class.java)
            mgr?.defaultVibrator
        } else {
            @Suppress("DEPRECATION")
            context.getSystemService(Vibrator::class.java)
        }
    }
    fun hapticClick() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            vibrator?.vibrate(VibrationEffect.createOneShot(15, VibrationEffect.DEFAULT_AMPLITUDE))
        }
    }

    // Touch input mapper
    val touchMapper = remember {
        TouchInputMapper(
            onMouseInput = { peerConnectionManager.dataChannelManager?.sendMouseInput(it) },
            onKeyboardInput = { peerConnectionManager.dataChannelManager?.sendKeyboardInput(it) }
        ).also { mapper ->
            mapper.fallbackScreenW = settings.hostScreenW
            mapper.fallbackScreenH = settings.hostScreenH
            mapper.trackpadSpeed = settings.trackpadSpeed
            mapper.scrollSpeed = settings.scrollSpeed
        }
    }

    // Collect screen_meta updates
    val dcManager = peerConnectionManager.dataChannelManager
    val screenMeta = dcManager?.screenMeta?.collectAsStateWithLifecycle()
    val hostDisplays = dcManager?.hostDisplays?.collectAsStateWithLifecycle()
    val rttMs = dcManager?.rttMs?.collectAsStateWithLifecycle()
    LaunchedEffect(screenMeta?.value) {
        screenMeta?.value?.let { meta ->
            touchMapper.screenMeta = meta
        }
    }

    // Renderer reference for cleanup and scaling control
    val rendererRef = remember { mutableStateOf<SurfaceViewRenderer?>(null) }

    // Sync modes to touch mapper
    LaunchedEffect(useAspectFill) {
        touchMapper.useAspectFill = useAspectFill
        Log.d(TAG, "Scaling mode: ${if (useAspectFill) "FILL" else "FIT"}")
    }
    LaunchedEffect(useTrackpad) {
        touchMapper.inputMode = if (useTrackpad) TouchInputMapper.InputMode.TRACKPAD else TouchInputMapper.InputMode.DIRECT_TOUCH
        Log.d(TAG, "Input mode: ${if (useTrackpad) "TRACKPAD" else "DIRECT_TOUCH"}")
    }

    // --- Connection orchestration ---
    LaunchedEffect(sessionId) {
        try {
            // Step 1: Initialize WebRTC
            statusText = "Initializing WebRTC..."
            // Rotating TURN creds из session response (null если server в legacy mode).
            val rotating = initialTurnServers?.map {
                Triple(it.urls, it.username, it.credential)
            }
            peerConnectionManager.initialize(
                stunUrl = settings.stunUrl,
                turnUrl = settings.turnUrl,
                turnUser = settings.turnUser,
                turnPass = settings.turnPass,
                rotatingTurnServers = rotating
            )

            // Step 2: Connect signaling WebSocket
            statusText = "Connecting to signaling..."
            signalingClient.connect(wsUrl, sessionId, wsToken)

            // Wait for WS connected
            signalingClient.connected.filter { it }.first()
            statusText = "Waiting for host..."

            // Step 3: Wait for host_ready (1.5s timeout, then proceed anyway)
            val hostReady = withTimeoutOrNull(1500) {
                signalingClient.messages.filter {
                    it.type == "peer_state" &&
                    it.payload["state"]?.jsonPrimitive?.content == "host_ready"
                }.first()
            }
            if (hostReady != null) {
                Log.d(TAG, "Host is ready")
            } else {
                Log.d(TAG, "Host ready timeout, proceeding with offer")
            }

            // Step 3.5: Unattended password handshake (port WPF UnattendedAuthCoord 2026-04-27).
            // Sends probe → waits mode → if password mode, prompts user → sends proof.
            // If host has password configured, viewer authenticates silently (no
            // confirmation dialog on host). If fallback (host not configured / probe
            // timeout) — host shows confirmation dialog as before.
            statusText = context.getString(com.zconect.viewer.R.string.unattended_status_authenticating)
            val authCoord = com.zconect.viewer.data.UnattendedAuthCoordinator(signalingClient, sessionId)
            // Saved password fast-path (audit fix #4 2026-04-27): для saved contacts
            // (contactId != null) подтягиваем previously saved password из SecurePrefs.
            // Coordinator пробует silent send first; если wrong → clears stale + falls
            // through to interactive prompt.
            val securePrefs = com.zconect.viewer.data.SecurePrefs.getInstance(context)
            val savedPassword = contactId?.let { securePrefs.getContactPassword(it) }
            val authOutcome = authCoord.run(
                messages = signalingClient.messages,
                passwordProvider = { promptInfo ->
                    val deferred = kotlinx.coroutines.CompletableDeferred<
                        com.zconect.viewer.data.UnattendedAuthCoordinator.PromptResult>()
                    unattendedPromptState = promptInfo to { result -> deferred.complete(result) }
                    val result = deferred.await()
                    unattendedPromptState = null
                    result
                },
                savedPassword = savedPassword,
                onSavedPasswordRejected = {
                    contactId?.let { securePrefs.removeContactPassword(it) }
                },
                onPasswordApproved = { password ->
                    contactId?.let { securePrefs.setContactPassword(it, password) }
                },
                canRemember = contactId != null,
            )
            when (authOutcome) {
                com.zconect.viewer.data.UnattendedAuthCoordinator.Outcome.LOCKED_OUT -> {
                    // Coordinator already showed lockout dialog с countdown — user
                    // dismissed. Just disconnect; статус прозвенит мгновенно.
                    Log.w(TAG, "unattended_locked_out — abort connection")
                    onDisconnected()
                    return@LaunchedEffect
                }
                com.zconect.viewer.data.UnattendedAuthCoordinator.Outcome.CANCELLED -> {
                    Log.d(TAG, "unattended_cancelled — user closed dialog, abort")
                    onDisconnected()
                    return@LaunchedEffect
                }
                com.zconect.viewer.data.UnattendedAuthCoordinator.Outcome.APPROVED -> {
                    Log.i(TAG, "unattended_approved — host won't show confirmation")
                }
                com.zconect.viewer.data.UnattendedAuthCoordinator.Outcome.FALLBACK -> {
                    Log.d(TAG, "unattended_fallback — host will show confirmation if needed")
                }
            }

            // Step 4: Create and send SDP offer (viewer = caller)
            statusText = "Negotiating connection..."
            peerConnectionManager.createOffer { offerSdp ->
                signalingClient.sendOffer(offerSdp)
                Log.d(TAG, "Offer sent")
            }

            // Step 5: Handle signaling messages
            launch {
                signalingClient.messages.collect { msg ->
                    handleSignalingMessage(msg, peerConnectionManager)
                }
            }

            // Forward local ICE candidates to signaling
            launch {
                peerConnectionManager.localIceCandidates.collect { candidate ->
                    signalingClient.sendIceCandidate(
                        candidate.sdp,
                        candidate.sdpMid ?: "0",
                        candidate.sdpMLineIndex
                    )
                }
            }

            // Step 6: Wait for data channel open (connectivity proof)
            statusText = "Establishing connection..."
            withTimeout(20_000) {
                peerConnectionManager.dataChannelOpen.first()
            }
            statusText = "Connected"
            isConnected = true

            // Step 7: Send initial settings
            delay(500)
            peerConnectionManager.dataChannelManager?.sendHostVideoSettingsRequest()
            peerConnectionManager.dataChannelManager?.sendHostDisplaysRequest(
                java.util.UUID.randomUUID().toString()
            )
            Log.d(TAG, "Initial settings sent")

            // Start RTT ping loop (every 2s)
            launch {
                while (isActive) {
                    delay(2000)
                    dcManager?.sendPing()
                }
            }

            // FPS counter — sample frames every second
            launch {
                var lastCount = 0
                while (isActive) {
                    delay(1000)
                    val cur = framesReceivedCount
                    fps = cur - lastCount
                    lastCount = cur
                }
            }

            // Auto-hide toolbar after 3 seconds (unless pinned)
            delay(3000)
            if (!toolbarPinned) showToolbar = false

        } catch (e: TimeoutCancellationException) {
            statusText = "Connection timeout"
            Log.e(TAG, "Connection timeout", e)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            statusText = "Error: ${e.message}"
            Log.e(TAG, "Connection error", e)
        }
    }

    // Handle disconnect with optional auto-reconnect
    LaunchedEffect(Unit) {
        signalingClient.disconnectReason.collect { reason ->
            if (!isConnected) return@collect
            isConnected = false

            if (!settings.autoReconnect) {
                statusText = "Disconnected: $reason"
                delay(3000)
                onDisconnected()
                return@collect
            }

            // Try to reconnect by re-joining session with saved login/pass
            val loginCode = settings.loginCode
            val passCode = settings.passCode
            if (loginCode.length != 8 || passCode.length != 8) {
                statusText = "Disconnected: $reason"
                delay(3000)
                onDisconnected()
                return@collect
            }

            val api = com.zconect.viewer.data.SessionApi(settings.httpBaseUrl)
            var success = false
            for (attempt in 1..3) {
                reconnectAttempt = attempt
                statusText = "Reconnecting ($attempt/3)..."
                val backoff = (1000L * (1 shl (attempt - 1))).coerceAtMost(8000L) // 1s, 2s, 4s
                delay(backoff)
                try {
                    val response = api.joinSession(loginCode, passCode)
                    // Tear down old signaling, reconnect with new token
                    signalingClient.disconnect()
                    delay(200)

                    // Bug fix 2026-04-27: применяем fresh rotating TURN creds на reconnect.
                    // Раньше TODO: response.turnServers получались но игнорировались —
                    // peerConnection держал старые creds в RTCConfiguration → после
                    // ~30 min TTL expiry новые ICE candidates не могли authenticate'ся
                    // к TURN relay → ICE state failed → user должен manually reconnect.
                    // Fix: reinitIceServers() recreates peerConnection с fresh creds
                    // без teardown factory/egl (~200ms экономии vs full close+initialize).
                    val rotating = response.turnServers?.map {
                        Triple(it.urls, it.username, it.credential)
                    }
                    peerConnectionManager.reinitIceServers(
                        stunUrl = settings.stunUrl,
                        turnUrl = settings.turnUrl,
                        turnUser = settings.turnUser,
                        turnPass = settings.turnPass,
                        rotatingTurnServers = rotating
                    )
                    Log.i(TAG, "Reconnect: peerConnection reinitialized с fresh ICE config (rotating=${rotating?.size ?: 0})")

                    val newWsUrl = "${settings.wsBaseUrl}${response.wsUrl}"
                    signalingClient.connect(newWsUrl, response.sessionId, response.wsToken)
                    // Wait for WS connected
                    withTimeoutOrNull(5000) { signalingClient.connected.filter { it }.first() }
                    if (signalingClient.connected.value) {
                        // Force new offer на freshly recreated peerConnection
                        peerConnectionManager.createOffer { offerSdp ->
                            signalingClient.sendOffer(offerSdp)
                        }
                        // Wait for data channel
                        withTimeoutOrNull(15_000) {
                            peerConnectionManager.dataChannelOpen.first()
                        }
                        success = true
                        break
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Reconnect attempt $attempt failed: ${e.message}")
                }
            }

            if (success) {
                statusText = "Connected"
                isConnected = true
                reconnectAttempt = 0
            } else {
                statusText = "Reconnect failed — please try again"
                reconnectAttempt = 0
                delay(3000)
                onDisconnected()
            }
        }
    }

    // Cleanup on exit (background thread to avoid hang)
    DisposableEffect(Unit) {
        onDispose {
            Thread {
                try {
                    val track = peerConnectionManager.remoteVideoTrack.value
                    val renderer = rendererRef.value
                    if (track != null && renderer != null) {
                        track.removeSink(renderer)
                    }
                    renderer?.release()
                } catch (_: Exception) {}
                signalingClient.disconnect()
                peerConnectionManager.close()
            }.start()
        }
    }

    // --- UI ---
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
            .systemBarsPadding()  // avoid content under status/nav bars and notch
    ) {
        // Video surface
        val currentVideoTrack = remoteVideoTrack
        if (currentVideoTrack != null) {
            var surfaceWidth by remember { mutableStateOf(0f) }
            var surfaceHeight by remember { mutableStateOf(0f) }

            BoxWithConstraints(
                modifier = Modifier.fillMaxSize(),
                contentAlignment = Alignment.Center
            ) {
                val density = LocalDensity.current
                val containerW = constraints.maxWidth.toFloat()
                val containerH = constraints.maxHeight.toFloat()
                val vw = videoWidth.toFloat().coerceAtLeast(1f)
                val vh = videoHeight.toFloat().coerceAtLeast(1f)

                // FIT: size box to video aspect ratio (shows all, black bars)
                // FILL: box = full container (video fills screen, may crop)
                val boxW: Float
                val boxH: Float
                if (useAspectFill) {
                    boxW = containerW
                    boxH = containerH
                } else {
                    val fitScale = min(containerW / vw, containerH / vh)
                    boxW = vw * fitScale
                    boxH = vh * fitScale
                }
                surfaceWidth = boxW
                surfaceHeight = boxH

                val boxWDp = with(density) { boxW.toDp() }
                val boxHDp = with(density) { boxH.toDp() }

                Box(
                    modifier = Modifier
                        .size(boxWDp, boxHDp)
                        .graphicsLayer(
                            scaleX = zoomScale,
                            scaleY = zoomScale,
                            translationX = zoomOffsetX,
                            translationY = zoomOffsetY
                        )
                ) {
                    // Video renderer — always ASPECT_FILL within its box
                    AndroidView(
                        factory = { ctx ->
                            SurfaceViewRenderer(ctx).apply {
                                init(eglBase.eglBaseContext, object : RendererCommon.RendererEvents {
                                    override fun onFirstFrameRendered() {
                                        Log.d(TAG, "First frame rendered")
                                    }
                                    override fun onFrameResolutionChanged(w: Int, h: Int, rotation: Int) {
                                        videoWidth = w
                                        videoHeight = h
                                        touchMapper.videoWidth = w
                                        touchMapper.videoHeight = h
                                        Log.d(TAG, "Video resolution: ${w}x${h}")
                                    }
                                })
                                setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FILL)
                                setEnableHardwareScaler(true)
                                rendererRef.value = this
                                // Wrap sink to count frames for FPS overlay
                                val frameCounter = org.webrtc.VideoSink { frame ->
                                    framesReceivedCount++
                                    this.onFrame(frame)
                                }
                                currentVideoTrack.addSink(frameCounter)
                            }
                        },
                        modifier = Modifier.fillMaxSize()
                    )

                    // Touch overlay — Microsoft RD style unified gesture handler.
                    //
                    // Gesture map (refactored 2026-04-27 после user report:
                    // double-tap не открывал папки, long-press не drag'ал окна,
                    // 2-finger pan не работал при zoom):
                    //
                    //   1 finger tap (any speed)        → left click at touch position
                    //   1 finger double-tap (≤450ms)    → DOUBLE-CLICK (открыть папку)
                    //   1 finger long-press (≥500ms)   → enter "hold-drag" mode →
                    //      next движение = left-button drag (window drag, text select)
                    //   1 finger long press без drag    → right click + cancel
                    //   1 finger move > threshold       → cursor move (trackpad)
                    //   2 finger tap                    → right click
                    //   2 finger drag (zoom == 1)       → scroll wheel
                    //   2 finger drag (zoom > 1)        → PAN view (clamp к границам)
                    //
                    // Tap recognition: jitter threshold 30px (было 12px — слишком
                    // агрессивно, fast tapping с palец shaking failed click). Double-tap
                    // window 450ms (было 350 — тесно для users с slow taps).
                    var lastTapTime by remember { mutableStateOf(0L) }
                    val longPressMs = 500L
                    val tapJitterPx = 30f
                    val doubleTapWindowMs = 450L

                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .pointerInput(useTrackpad) {
                                awaitEachGesture {
                                    val down = awaitFirstDown()
                                    down.consume()
                                    val startPos = down.position
                                    val startTime = System.currentTimeMillis()
                                    val isDoubleTap = (startTime - lastTapTime) < doubleTapWindowMs

                                    var fingerCount = 1
                                    var totalMove = 0f
                                    var lastX = startPos.x
                                    var lastY = startPos.y
                                    var scrollPrevY = startPos.y
                                    var panPrevX = startPos.x
                                    var panPrevY = startPos.y
                                    var scrollTotal = 0f
                                    var movedBeyondJitter = false
                                    var holdDragMode = false
                                    var rightClickFired = false

                                    // Main event loop. Long-press detection происходит
                                    // через elapsed-time check внутри loop'а — без
                                    // separate timer (keeps it simple, awaitPointerEvent
                                    // wakes us on every frame anyway).
                                    while (true) {
                                        val event = awaitPointerEvent()
                                        val pressed = event.changes.filter { it.pressed }
                                        if (pressed.isEmpty()) break

                                        val currentFingers = pressed.size
                                        if (currentFingers > fingerCount) fingerCount = currentFingers
                                        val elapsed = System.currentTimeMillis() - startTime

                                        if (fingerCount >= 2) {
                                            // ═══ TWO+ FINGERS ═══
                                            // Когда zoomed (scale > 1) — 2-finger drag panет view.
                                            // Иначе — scroll wheel.
                                            val avgX = pressed.map { it.position.x }.average().toFloat()
                                            val avgY = pressed.map { it.position.y }.average().toFloat()
                                            if (!movedBeyondJitter) {
                                                scrollPrevY = avgY
                                                panPrevX = avgX
                                                panPrevY = avgY
                                                movedBeyondJitter = true
                                            }
                                            if (zoomScale > 1f) {
                                                // Pan view (clamp чтобы не уехать за границы видео).
                                                val dpx = avgX - panPrevX
                                                val dpy = avgY - panPrevY
                                                if (kotlin.math.abs(dpx) > 0.5f || kotlin.math.abs(dpy) > 0.5f) {
                                                    val maxOffsetX = surfaceWidth * (zoomScale - 1f) / 2f
                                                    val maxOffsetY = surfaceHeight * (zoomScale - 1f) / 2f
                                                    zoomOffsetX = (zoomOffsetX + dpx).coerceIn(-maxOffsetX, maxOffsetX)
                                                    zoomOffsetY = (zoomOffsetY + dpy).coerceIn(-maxOffsetY, maxOffsetY)
                                                    panPrevX = avgX
                                                    panPrevY = avgY
                                                }
                                            } else {
                                                val dy = avgY - scrollPrevY
                                                scrollTotal += kotlin.math.abs(dy)
                                                if (kotlin.math.abs(dy) > 1.5f) {
                                                    touchMapper.sendScroll(dy)
                                                    scrollPrevY = avgY
                                                }
                                            }
                                            event.changes.forEach { it.consume() }
                                        } else {
                                            // ═══ ONE FINGER ═══
                                            val change = pressed.first()
                                            val dx = change.position.x - lastX
                                            val dy = change.position.y - lastY
                                            totalMove += kotlin.math.abs(dx) + kotlin.math.abs(dy)

                                            // Long-press detection: holding >500ms без движения.
                                            // Тогда entering hold-drag mode (left button down).
                                            // Subsequent movement = drag.
                                            if (!holdDragMode && !isDoubleTap && totalMove < tapJitterPx
                                                && elapsed >= longPressMs && !rightClickFired) {
                                                // Vibrate + start hold-drag
                                                hapticClick()
                                                holdDragMode = true
                                                touchMapper.sendLeftDragStart()
                                            }

                                            // Double-tap immediate hold-drag activation.
                                            if (!holdDragMode && isDoubleTap && useTrackpad
                                                && totalMove >= tapJitterPx) {
                                                hapticClick()
                                                holdDragMode = true
                                                touchMapper.sendLeftDragStart()
                                            }

                                            if (totalMove > tapJitterPx) {
                                                movedBeyondJitter = true
                                                if (holdDragMode) {
                                                    touchMapper.sendLeftDragMove(dx, dy)
                                                } else {
                                                    touchMapper.sendDragMove(
                                                        change.position.x, change.position.y,
                                                        dx, dy, surfaceWidth, surfaceHeight
                                                    )
                                                }
                                                val pos = touchMapper.getCursorScreenPosition(surfaceWidth, surfaceHeight)
                                                if (pos != null) {
                                                    cursorX = pos.first; cursorY = pos.second; cursorVisible = true
                                                }
                                            }
                                            lastX = change.position.x
                                            lastY = change.position.y
                                            change.consume()
                                        }
                                    }

                                    // ═══ Gesture ended ═══
                                    val elapsed = System.currentTimeMillis() - startTime

                                    if (holdDragMode) {
                                        // End left-button drag (window drag/select complete)
                                        touchMapper.sendLeftDragEnd()
                                        lastTapTime = 0L
                                    } else if (fingerCount >= 2) {
                                        // Two-finger tap (no significant movement) = right click.
                                        // Used scrollTotal вместо totalMove because pan/scroll
                                        // accumulates differently.
                                        if (scrollTotal < 25f && elapsed < 400) {
                                            hapticClick()
                                            touchMapper.sendRightClick(startPos.x, startPos.y, surfaceWidth, surfaceHeight)
                                            val pos = touchMapper.getCursorScreenPosition(surfaceWidth, surfaceHeight)
                                            if (pos != null) { cursorX = pos.first; cursorY = pos.second; cursorVisible = true }
                                        }
                                        lastTapTime = 0L
                                    } else if (totalMove < tapJitterPx) {
                                        // Single-finger tap без significant movement.
                                        if (isDoubleTap) {
                                            // DOUBLE-CLICK — open folder, expand selection, etc.
                                            hapticClick()
                                            touchMapper.sendTap(startPos.x, startPos.y, surfaceWidth, surfaceHeight)
                                            touchMapper.sendTap(startPos.x, startPos.y, surfaceWidth, surfaceHeight)
                                            lastTapTime = 0L  // consumed
                                            val pos = touchMapper.getCursorScreenPosition(surfaceWidth, surfaceHeight)
                                            if (pos != null) { cursorX = pos.first; cursorY = pos.second; cursorVisible = true }
                                        } else if (elapsed >= longPressMs) {
                                            // Long press без drag → right click.
                                            hapticClick()
                                            touchMapper.sendRightClick(startPos.x, startPos.y, surfaceWidth, surfaceHeight)
                                            lastTapTime = 0L
                                            val pos = touchMapper.getCursorScreenPosition(surfaceWidth, surfaceHeight)
                                            if (pos != null) { cursorX = pos.first; cursorY = pos.second; cursorVisible = true }
                                        } else {
                                            // Quick single tap = left click.
                                            hapticClick()
                                            touchMapper.sendTap(startPos.x, startPos.y, surfaceWidth, surfaceHeight)
                                            lastTapTime = System.currentTimeMillis()
                                            val pos = touchMapper.getCursorScreenPosition(surfaceWidth, surfaceHeight)
                                            if (pos != null) { cursorX = pos.first; cursorY = pos.second; cursorVisible = true }
                                            // Toggle toolbar on tap near top.
                                            if (startPos.y < surfaceHeight * 0.08f && !toolbarPinned) {
                                                showToolbar = !showToolbar
                                            }
                                        }
                                    } else {
                                        // 1-finger drag без entering hold-drag → just cursor move.
                                        lastTapTime = 0L
                                    }
                                }
                            }
                    )

                    // Cursor indicator overlay (crosshair for both modes)
                    if (cursorVisible && cursorX >= 0f) {
                        Canvas(modifier = Modifier.fillMaxSize()) {
                            val pos = Offset(cursorX, cursorY)
                            // Outer circle
                            drawCircle(Color.White, 16f, pos, style = Stroke(width = 2.5f))
                            // Inner dot
                            drawCircle(
                                if (useTrackpad) Color(0xFF4FC3F7) else Color(0xFFFF9800),
                                5f, pos
                            )
                            // Crosshair
                            val len = 22f; val gap = 9f
                            drawLine(Color.White, Offset(pos.x - len, pos.y), Offset(pos.x - gap, pos.y), strokeWidth = 1.5f)
                            drawLine(Color.White, Offset(pos.x + gap, pos.y), Offset(pos.x + len, pos.y), strokeWidth = 1.5f)
                            drawLine(Color.White, Offset(pos.x, pos.y - len), Offset(pos.x, pos.y - gap), strokeWidth = 1.5f)
                            drawLine(Color.White, Offset(pos.x, pos.y + gap), Offset(pos.x, pos.y + len), strokeWidth = 1.5f)
                        }
                    }
                }

            }
        } else {
            // No video yet — show status
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    CircularProgressIndicator(color = Color.White)
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(statusText, color = Color.White)
                }
            }
        }

        // Swipe-from-top area — drag down to show toolbar
        if (!showToolbar) {
            Box(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .height(40.dp)
                    .pointerInput(Unit) {
                        var totalDy = 0f
                        awaitEachGesture {
                            awaitFirstDown()
                            totalDy = 0f
                            while (true) {
                                val event = awaitPointerEvent()
                                val change = event.changes.firstOrNull() ?: break
                                if (!change.pressed) break
                                totalDy += change.position.y - (change.previousPosition.y)
                                if (totalDy > 30f) {
                                    showToolbar = true
                                    break
                                }
                                change.consume()
                            }
                        }
                    }
            )
        }

        // Status overlay (RTT, FPS)
        if (showStatsOverlay) {
            Surface(
                color = Color(0xCC000000),
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(horizontal = 8.dp, vertical = 56.dp)
            ) {
                Column(modifier = Modifier.padding(8.dp)) {
                    val rtt = rttMs?.value ?: -1
                    val rttColor = when {
                        rtt < 0 -> Color.Gray
                        rtt < 60 -> Color(0xFF81C784)
                        rtt < 150 -> Color(0xFFFFD54F)
                        else -> Color(0xFFE57373)
                    }
                    Text(
                        text = if (rtt < 0) "RTT: --" else "RTT: ${rtt}ms",
                        color = rttColor,
                        style = MaterialTheme.typography.bodySmall
                    )
                    Text(
                        text = "FPS: $fps",
                        color = Color.White,
                        style = MaterialTheme.typography.bodySmall
                    )
                    Text(
                        text = "${videoWidth}×${videoHeight}",
                        color = Color.White,
                        style = MaterialTheme.typography.bodySmall
                    )
                    if (reconnectAttempt > 0) {
                        Text(
                            text = "Reconnect #$reconnectAttempt",
                            color = Color(0xFFFFB74D),
                            style = MaterialTheme.typography.bodySmall
                        )
                    }
                }
            }
        }

        // Floating toolbar (top)
        AnimatedVisibility(
            visible = showToolbar,
            enter = slideInVertically(initialOffsetY = { -it }) + fadeIn(),
            exit = slideOutVertically(targetOffsetY = { -it }) + fadeOut(),
            modifier = Modifier.align(Alignment.TopCenter)
        ) {
            Surface(
                color = Color(0xF01565C0), // strong blue, 94% opacity — visible on any background
                shadowElevation = 8.dp,
                modifier = Modifier
                    .fillMaxWidth()
            ) {
                // 2-row layout (UX fix 2026-04-27): раньше 13 IconButton'ов + status в
                // одном Row не помещались в portrait (~360dp). Теперь:
                // Row 1 — status text (full-width, ellipsis)
                // Row 2 — buttons в horizontalScroll (umещаются в любую ширину)
                Column(modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp)) {
                    Text(
                        text = statusText,
                        style = MaterialTheme.typography.bodySmall,
                        color = Color.White,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 2.dp)
                    )
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .horizontalScroll(rememberScrollState()),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {

                    // Input mode toggle: Trackpad ↔ Direct Touch
                    IconButton(onClick = { useTrackpad = !useTrackpad }) {
                        Icon(
                            if (useTrackpad) Icons.Default.Mouse else Icons.Default.TouchApp,
                            contentDescription = if (useTrackpad) "Trackpad mode" else "Direct touch mode",
                            tint = Color.White
                        )
                    }

                    // Scaling mode toggle: FILL ↔ FIT
                    IconButton(onClick = { useAspectFill = !useAspectFill }) {
                        Icon(
                            if (useAspectFill) Icons.Default.FitScreen else Icons.Default.Fullscreen,
                            contentDescription = if (useAspectFill) "Fit to screen" else "Fill screen",
                            tint = Color.White
                        )
                    }

                    // Quality preset menu
                    Box {
                        IconButton(onClick = { showQualityMenu = true }) {
                            Icon(
                                Icons.Default.Tune,
                                contentDescription = "Video quality",
                                tint = Color.White
                            )
                        }
                        DropdownMenu(
                            expanded = showQualityMenu,
                            onDismissRequest = { showQualityMenu = false }
                        ) {
                            listOf("Auto", "High", "Medium", "Low", "Extra Low").forEach { preset ->
                                DropdownMenuItem(
                                    text = { Text(preset) },
                                    leadingIcon = {
                                        if (preset == qualityPreset) {
                                            Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                                        } else {
                                            Spacer(modifier = Modifier.size(18.dp))
                                        }
                                    },
                                    onClick = {
                                        qualityPreset = preset
                                        showQualityMenu = false
                                        dcManager?.sendHostVideoSettingsRequest(
                                            com.zconect.viewer.webrtc.HostVideoSettingsRequest(
                                                qualityPreset = preset,
                                                displayMode = "Current",
                                                displayId = selectedDisplayId,
                                                quickReconnect = false
                                            )
                                        )
                                    }
                                )
                            }
                        }
                    }

                    // Monitor selector menu
                    Box {
                        IconButton(onClick = {
                            dcManager?.sendHostDisplaysRequest(java.util.UUID.randomUUID().toString())
                            showMonitorMenu = true
                        }) {
                            Icon(
                                Icons.Default.Monitor,
                                contentDescription = "Select monitor",
                                tint = Color.White
                            )
                        }
                        DropdownMenu(
                            expanded = showMonitorMenu,
                            onDismissRequest = { showMonitorMenu = false }
                        ) {
                            val displays = hostDisplays?.value ?: emptyList()
                            if (displays.isEmpty()) {
                                DropdownMenuItem(
                                    text = { Text("Loading displays...") },
                                    onClick = { showMonitorMenu = false },
                                    enabled = false
                                )
                            } else {
                                // "All displays" option
                                DropdownMenuItem(
                                    text = { Text("All Displays") },
                                    leadingIcon = {
                                        if (selectedDisplayId == "all") {
                                            Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                                        } else Spacer(modifier = Modifier.size(18.dp))
                                    },
                                    onClick = {
                                        selectedDisplayId = "all"
                                        showMonitorMenu = false
                                        dcManager?.sendHostVideoSettingsRequest(
                                            com.zconect.viewer.webrtc.HostVideoSettingsRequest(
                                                qualityPreset = qualityPreset,
                                                displayMode = "All",
                                                displayId = "",
                                                quickReconnect = false
                                            )
                                        )
                                    }
                                )
                                displays.forEach { display ->
                                    DropdownMenuItem(
                                        text = {
                                            Text(
                                                if (display.isPrimary) "${display.name} ★"
                                                else display.name
                                            )
                                        },
                                        leadingIcon = {
                                            if (selectedDisplayId == display.id) {
                                                Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                                            } else Spacer(modifier = Modifier.size(18.dp))
                                        },
                                        onClick = {
                                            selectedDisplayId = display.id
                                            showMonitorMenu = false
                                            dcManager?.sendHostVideoSettingsRequest(
                                                com.zconect.viewer.webrtc.HostVideoSettingsRequest(
                                                    qualityPreset = qualityPreset,
                                                    displayMode = "Current",
                                                    displayId = display.id,
                                                    quickReconnect = false
                                                )
                                            )
                                        }
                                    )
                                }
                            }
                        }
                    }

                    IconButton(onClick = { showKeyboard = !showKeyboard }) {
                        Icon(
                            Icons.Default.Keyboard,
                            contentDescription = "Toggle keyboard",
                            tint = if (showKeyboard) Color(0xFFFFEB3B) else Color.White
                        )
                    }

                    // Zoom out
                    IconButton(onClick = {
                        zoomScale = (zoomScale / 1.3f).coerceAtLeast(1f)
                        if (zoomScale == 1f) { zoomOffsetX = 0f; zoomOffsetY = 0f }
                    }, enabled = zoomScale > 1f) {
                        Icon(
                            Icons.Default.ZoomOut,
                            contentDescription = "Zoom out",
                            tint = if (zoomScale > 1f) Color.White else Color.White.copy(alpha = 0.4f)
                        )
                    }

                    // Zoom in
                    IconButton(onClick = {
                        zoomScale = (zoomScale * 1.3f).coerceAtMost(4f)
                    }, enabled = zoomScale < 4f) {
                        Icon(
                            Icons.Default.ZoomIn,
                            contentDescription = "Zoom in",
                            tint = if (zoomScale < 4f) Color.White else Color.White.copy(alpha = 0.4f)
                        )
                    }

                    // Stats overlay toggle
                    IconButton(onClick = { showStatsOverlay = !showStatsOverlay }) {
                        Icon(
                            Icons.Default.Speed,
                            contentDescription = "Connection stats",
                            tint = if (showStatsOverlay) Color(0xFFFFEB3B) else Color.White
                        )
                    }

                    // Help / gesture cheat sheet
                    IconButton(onClick = { showHelpDialog = true }) {
                        Icon(
                            Icons.AutoMirrored.Filled.HelpOutline,
                            contentDescription = "Gestures help",
                            tint = Color.White
                        )
                    }

                    // Pin toolbar
                    IconButton(onClick = { toolbarPinned = !toolbarPinned }) {
                        Icon(
                            if (toolbarPinned) Icons.Default.PushPin else Icons.Outlined.PushPin,
                            contentDescription = if (toolbarPinned) "Unpin toolbar" else "Pin toolbar",
                            tint = if (toolbarPinned) Color(0xFFFFEB3B) else Color.White
                        )
                    }

                    // Hide toolbar
                    IconButton(onClick = { showToolbar = false }) {
                        Icon(
                            Icons.Default.VisibilityOff,
                            contentDescription = "Hide toolbar",
                            tint = Color.White
                        )
                    }

                    // Disconnect
                    IconButton(onClick = {
                        Thread {
                            signalingClient.disconnect()
                            peerConnectionManager.close()
                        }.start()
                        onDisconnected()
                    }) {
                        Icon(
                            Icons.Default.Close,
                            contentDescription = "Disconnect",
                            tint = Color(0xFFFFCDD2)
                        )
                    }
                    } // end Row (buttons)
                } // end Column (status + buttons)
            }
        }

        // KeyboardBar (bottom)
        AnimatedVisibility(
            visible = showKeyboard,
            enter = slideInVertically(initialOffsetY = { it }) + fadeIn(),
            exit = slideOutVertically(targetOffsetY = { it }) + fadeOut(),
            modifier = Modifier.align(Alignment.BottomCenter)
        ) {
            KeyboardBar(
                onVirtualKey = { vk, ctrl, alt, shift, win ->
                    touchMapper.sendVirtualKey(vk, ctrl, alt, shift, win)
                },
                onCtrlAltDel = {
                    peerConnectionManager.dataChannelManager?.sendCtrlAltDel()
                },
                onShowSystemKeyboard = {
                    // Toggle: tap первый раз показывает system keyboard, tap второй — скрывает.
                    // Раньше ABC button только показывал → user'у негде было его убрать
                    // кроме Back button (который часто closes app).
                    isSystemKeyboardVisible = !isSystemKeyboardVisible
                    keyboardTrigger++
                }
            )
        }

        // Hidden input capture через custom InputConnection (bypass Compose state).
        //
        // Fix attempt #4 после 3 неудачных (autoCorrect=false, anchor reset,
        // AtomicReference + time dedup). Root cause: BasicTextField привязывает
        // text state к IME через TextFieldValue — любой state update может
        // триггерить IME re-emit, вызывая duplicates.
        //
        // Решение: KeyCaptureView — plain Android EditText с custom BaseInputConnection
        // который НЕ модифицирует EditText buffer (super.commitText НЕ вызывается).
        // EditText всегда empty → IME не имеет context для autocorrect. State
        // (composingText) live inside IC, synchronous per contract Android IME API.
        // Это bulletproof approach, используется в VNC Viewer / Microsoft RDP.
        AndroidView(
            factory = { ctx ->
                KeyCaptureView(ctx).apply {
                    onCharacter = { ch -> touchMapper.sendCharacter(ch) }
                    onBackspace = { touchMapper.sendVirtualKey(0x08) }
                    onEnter = { touchMapper.sendVirtualKey(0x0D) }
                    keyCaptureViewRef.set(this)
                }
            },
            modifier = Modifier.size(1.dp),
        )

        // Show/hide system keyboard через KeyCaptureView direct + InputMethodManager.
        // Avoids LocalSoftwareKeyboardController (которая привязывается к
        // BasicTextField'у, а не к нашему AndroidView).
        LaunchedEffect(keyboardTrigger) {
            if (keyboardTrigger > 0) {
                val view = keyCaptureViewRef.get() ?: return@LaunchedEffect
                val imm = context.getSystemService(InputMethodManager::class.java)
                if (isSystemKeyboardVisible) {
                    view.requestFocus()
                    delay(100)
                    imm?.showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)
                } else {
                    imm?.hideSoftInputFromWindow(view.windowToken, 0)
                    view.clearFocus()
                }
            }
        }

        // Mini FAB to show toolbar (when toolbar is hidden)
        if (!showToolbar && !showKeyboard) {
            FloatingActionButton(
                onClick = { showToolbar = true },
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(16.dp)
                    .size(44.dp),
                containerColor = Color(0xF01565C0), // matches toolbar
                contentColor = Color.White
            ) {
                Icon(
                    Icons.Default.MoreVert,
                    contentDescription = "Show toolbar",
                    modifier = Modifier.size(22.dp)
                )
            }
        }

        // Gesture help dialog
        if (showHelpDialog) {
            GestureHelpDialog(
                useTrackpad = useTrackpad,
                onDismiss = { showHelpDialog = false }
            )
        }

        // Unattended password prompt — shown во время connect handshake если
        // host имеет password configured (port WPF UnattendedPasswordDialog 2026-04-27).
        unattendedPromptState?.let { (prompt, callback) ->
            UnattendedPasswordDialog(
                prompt = prompt,
                onSubmit = { password, remember ->
                    callback(com.zconect.viewer.data.UnattendedAuthCoordinator.PromptResult(
                        password = password, remember = remember,
                    ))
                },
                onCancel = {
                    callback(com.zconect.viewer.data.UnattendedAuthCoordinator.PromptResult(password = null))
                },
            )
        }
    }
}

private fun handleSignalingMessage(
    msg: SignalingMessage,
    peerConnectionManager: PeerConnectionManager
) {
    when (msg.type) {
        "answer" -> {
            val sdp = msg.payload["sdp"]?.jsonPrimitive?.content ?: return
            Log.d(TAG, "Received SDP answer")
            peerConnectionManager.setRemoteAnswer(sdp)
        }
        "ice" -> {
            val candidate = msg.payload["candidate"]?.jsonPrimitive?.content ?: return
            val sdpMid = msg.payload["sdpMid"]?.jsonPrimitive?.content ?: "0"
            val sdpMLineIndex = msg.payload["sdpMLineIndex"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0
            peerConnectionManager.addIceCandidate(candidate, sdpMid, sdpMLineIndex)
        }
        "peer_disconnected" -> {
            Log.d(TAG, "Host disconnected")
        }
    }
}
