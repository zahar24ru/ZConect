package com.zconect.viewer.webrtc

import android.content.Context
import android.util.Log
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import org.webrtc.*

/**
 * Manages WebRTC PeerConnection lifecycle: SDP offer/answer, ICE candidates, data channels.
 * Viewer is the CALLER — creates the offer and data channels.
 */
class PeerConnectionManager(private val context: Context) {

    companion object {
        private const val TAG = "PeerConnMgr"
    }

    private var factory: PeerConnectionFactory? = null
    private var peerConnection: PeerConnection? = null
    private var eglBase: EglBase? = null

    // Data channels (created by viewer = caller)
    private var dcControl: DataChannel? = null
    private var dcInput: DataChannel? = null
    private var dcClipboard: DataChannel? = null
    private var dcFile: DataChannel? = null

    var dataChannelManager: DataChannelManager? = null
        private set

    // State flows
    private val _remoteVideoTrack = MutableStateFlow<VideoTrack?>(null)
    val remoteVideoTrack: StateFlow<VideoTrack?> = _remoteVideoTrack

    private val _iceState = MutableStateFlow(PeerConnection.IceConnectionState.NEW)
    val iceState: StateFlow<PeerConnection.IceConnectionState> = _iceState

    private val _localIceCandidates = MutableSharedFlow<IceCandidate>(extraBufferCapacity = 64)
    val localIceCandidates: SharedFlow<IceCandidate> = _localIceCandidates

    private val _dataChannelOpen = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    val dataChannelOpen: SharedFlow<Unit> = _dataChannelOpen

    fun getEglBase(): EglBase {
        if (eglBase == null) {
            eglBase = EglBase.create()
        }
        return eglBase!!
    }

    fun initialize(
        stunUrl: String = "",
        turnUrl: String = "",
        turnUser: String = "",
        turnPass: String = "",
        /**
         * Rotating TURN credentials из session response (RFC 7635). Если не null
         * и не пуст — перекрывает static turnUrl/turnUser/turnPass полностью.
         * STUN всегда берётся из stunUrl.
         *
         * Формат совпадает с [com.zconect.viewer.data.TurnServerConfig].
         * Делаем parameter как `List<Triple<urls, username, credential>>` чтобы избежать
         * cross-package dependency на data layer; caller конвертирует.
         */
        rotatingTurnServers: List<Triple<List<String>, String, String>>? = null
    ) {
        Log.d(TAG, "Initializing WebRTC (STUN=$stunUrl, TURN=$turnUrl, rotating=${rotatingTurnServers?.size ?: 0})")

        val egl = getEglBase()

        // Initialize PeerConnectionFactory
        PeerConnectionFactory.initialize(
            PeerConnectionFactory.InitializationOptions.builder(context)
                .setEnableInternalTracer(false)
                .createInitializationOptions()
        )

        val encoderFactory = DefaultVideoEncoderFactory(egl.eglBaseContext, true, true)
        val decoderFactory = DefaultVideoDecoderFactory(egl.eglBaseContext)

        factory = PeerConnectionFactory.builder()
            .setVideoEncoderFactory(encoderFactory)
            .setVideoDecoderFactory(decoderFactory)
            .createPeerConnectionFactory()

        // ICE servers: STUN всегда + TURN (rotating если server выдал, иначе static).
        val iceServers = mutableListOf<PeerConnection.IceServer>()
        iceServers.add(PeerConnection.IceServer.builder(stunUrl).createIceServer())

        if (!rotatingTurnServers.isNullOrEmpty()) {
            // Server в rotating mode — используем присланные creds. Static config ignored.
            // Hardening (security audit 2026-04-24 M3): whitelist TURN/STUN schemes —
            // защита от compromised signaling который мог бы inject'нуть javascript:/data:/file:
            // URLs или attacker-controlled TURN host для IP exfiltration.
            var addedCount = 0
            for ((urls, user, cred) in rotatingTurnServers) {
                for (u in urls) {
                    val lower = u.lowercase()
                    if (!lower.startsWith("turn:") && !lower.startsWith("turns:") &&
                        !lower.startsWith("stun:") && !lower.startsWith("stuns:")) {
                        Log.w(TAG, "Rejected non-TURN/STUN scheme from server: $u")
                        continue
                    }
                    if (u.length > 1024) {
                        Log.w(TAG, "Rejected oversized URL from server (${u.length} chars)")
                        continue
                    }
                    iceServers.add(
                        PeerConnection.IceServer.builder(u)
                            .setUsername(user)
                            .setPassword(cred)
                            .createIceServer()
                    )
                    addedCount++
                }
            }
            Log.i(TAG, "Using rotating TURN creds ($addedCount ICE servers added)")
            // Если ничего не прошло валидацию — добавляем static fallback
            if (addedCount == 0) {
                Log.w(TAG, "All rotating TURN URLs rejected — falling back to static")
                iceServers.add(
                    PeerConnection.IceServer.builder(turnUrl)
                        .setUsername(turnUser)
                        .setPassword(turnPass)
                        .createIceServer()
                )
            }
        } else {
            // Legacy static mode
            iceServers.add(
                PeerConnection.IceServer.builder(turnUrl)
                    .setUsername(turnUser)
                    .setPassword(turnPass)
                    .createIceServer()
            )
        }

        val rtcConfig = PeerConnection.RTCConfiguration(iceServers).apply {
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            rtcpMuxPolicy = PeerConnection.RtcpMuxPolicy.REQUIRE
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        }

        peerConnection = factory!!.createPeerConnection(rtcConfig, peerConnectionObserver)
            ?: throw RuntimeException("Failed to create PeerConnection")

        // Add video transceiver as RECV_ONLY (viewer only receives video)
        peerConnection!!.addTransceiver(
            MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO,
            RtpTransceiver.RtpTransceiverInit(RtpTransceiver.RtpTransceiverDirection.RECV_ONLY)
        )

        // Create 4 data channels (viewer = caller creates them)
        val dcInit = DataChannel.Init().apply {
            ordered = true
            // reliable mode (no maxRetransmits set)
        }

        dcControl = peerConnection!!.createDataChannel("dc-control", dcInit)
        dcInput = peerConnection!!.createDataChannel("dc-input", DataChannel.Init().apply { ordered = true })
        dcClipboard = peerConnection!!.createDataChannel("dc-clipboard", DataChannel.Init().apply { ordered = true })
        dcFile = peerConnection!!.createDataChannel("dc-file", DataChannel.Init().apply { ordered = true })

        // Setup DataChannelManager
        dataChannelManager = DataChannelManager().also { mgr ->
            mgr.onChannelOpen = { _dataChannelOpen.tryEmit(Unit) }
            dcControl?.let { mgr.attachChannel("dc-control", it) }
            dcInput?.let { mgr.attachChannel("dc-input", it) }
            dcClipboard?.let { mgr.attachChannel("dc-clipboard", it) }
            dcFile?.let { mgr.attachChannel("dc-file", it) }
        }

        Log.d(TAG, "WebRTC initialized, 4 data channels created")
    }

    fun createOffer(callback: (String) -> Unit) {
        val constraints = MediaConstraints().apply {
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveVideo", "true"))
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveAudio", "false"))
        }

        peerConnection?.createOffer(object : SdpObserver {
            override fun onCreateSuccess(sdp: SessionDescription) {
                Log.d(TAG, "Offer created")
                peerConnection?.setLocalDescription(object : SdpObserver {
                    override fun onSetSuccess() {
                        Log.d(TAG, "Local description set")
                        callback(sdp.description)
                    }
                    override fun onSetFailure(error: String?) { Log.e(TAG, "Set local desc failed: $error") }
                    override fun onCreateSuccess(p0: SessionDescription?) {}
                    override fun onCreateFailure(p0: String?) {}
                }, sdp)
            }
            override fun onCreateFailure(error: String?) { Log.e(TAG, "Create offer failed: $error") }
            override fun onSetSuccess() {}
            override fun onSetFailure(p0: String?) {}
        }, constraints)
    }

    fun setRemoteAnswer(sdp: String) {
        val sessionDesc = SessionDescription(SessionDescription.Type.ANSWER, sdp)
        peerConnection?.setRemoteDescription(object : SdpObserver {
            override fun onSetSuccess() { Log.d(TAG, "Remote answer set") }
            override fun onSetFailure(error: String?) { Log.e(TAG, "Set remote answer failed: $error") }
            override fun onCreateSuccess(p0: SessionDescription?) {}
            override fun onCreateFailure(p0: String?) {}
        }, sessionDesc)
    }

    fun addIceCandidate(candidate: String, sdpMid: String, sdpMLineIndex: Int) {
        val iceCandidate = IceCandidate(sdpMid, sdpMLineIndex, candidate)
        peerConnection?.addIceCandidate(iceCandidate)
    }

    fun close() {
        Log.d(TAG, "Closing PeerConnection")
        dcControl?.close()
        dcInput?.close()
        dcClipboard?.close()
        dcFile?.close()
        peerConnection?.close()
        peerConnection?.dispose()
        peerConnection = null
        factory?.dispose()
        factory = null
        eglBase?.release()
        eglBase = null
        _remoteVideoTrack.value = null
        dataChannelManager = null
    }

    /**
     * Bug fix 2026-04-27: applies fresh ICE servers (rotating TURN creds) на reconnect
     * без полного teardown factory/egl. Иначе peerConnection держал старые credentials
     * в RTCConfiguration → после ~30 min TTL expiry новые ICE candidates не могли
     * authenticate'ся к TURN relay → ICE state failed → user должен manually reconnect.
     *
     * Closes old peerConnection (frees ICE/SCTP resources) и создаёт новое с fresh
     * ICE servers. Factory + eglBase reused — экономит ~200ms на reconnect.
     *
     * Caller must:
     * 1. Re-create offer через createOffer(...) — old offer/answer state cleared.
     * 2. Wait dataChannelOpen.first() для new connection proof.
     *
     * Note: localIceCandidates / dataChannelOpen — это MutableSharedFlow на
     * PeerConnectionManager итself (не на peerConnection inside), поэтому
     * existing collectors continue работать без re-subscribe.
     */
    fun reinitIceServers(
        stunUrl: String,
        turnUrl: String,
        turnUser: String,
        turnPass: String,
        rotatingTurnServers: List<Triple<List<String>, String, String>>? = null
    ) {
        Log.i(TAG, "Reinitializing ICE servers (rotating=${rotatingTurnServers?.size ?: 0})")

        // Tear down peer (но not factory/egl — reuse).
        dcControl?.close(); dcControl = null
        dcInput?.close(); dcInput = null
        dcClipboard?.close(); dcClipboard = null
        dcFile?.close(); dcFile = null
        peerConnection?.close()
        peerConnection?.dispose()
        peerConnection = null
        _remoteVideoTrack.value = null
        dataChannelManager = null

        // Build fresh ICE servers (same logic as initialize).
        val iceServers = mutableListOf<PeerConnection.IceServer>()
        if (stunUrl.isNotEmpty()) {
            iceServers.add(PeerConnection.IceServer.builder(stunUrl).createIceServer())
        }
        if (!rotatingTurnServers.isNullOrEmpty()) {
            for ((urls, user, cred) in rotatingTurnServers) {
                for (u in urls) {
                    val lower = u.lowercase()
                    if (!lower.startsWith("turn:") && !lower.startsWith("turns:") &&
                        !lower.startsWith("stun:") && !lower.startsWith("stuns:")) continue
                    if (u.length > 1024) continue
                    iceServers.add(
                        PeerConnection.IceServer.builder(u)
                            .setUsername(user)
                            .setPassword(cred)
                            .createIceServer()
                    )
                }
            }
        } else if (turnUrl.isNotEmpty()) {
            iceServers.add(
                PeerConnection.IceServer.builder(turnUrl)
                    .setUsername(turnUser)
                    .setPassword(turnPass)
                    .createIceServer()
            )
        }

        val rtcConfig = PeerConnection.RTCConfiguration(iceServers).apply {
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            rtcpMuxPolicy = PeerConnection.RtcpMuxPolicy.REQUIRE
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        }

        peerConnection = factory!!.createPeerConnection(rtcConfig, peerConnectionObserver)
            ?: throw RuntimeException("Failed to recreate PeerConnection on reinit")

        peerConnection!!.addTransceiver(
            MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO,
            RtpTransceiver.RtpTransceiverInit(RtpTransceiver.RtpTransceiverDirection.RECV_ONLY)
        )

        dcControl = peerConnection!!.createDataChannel("dc-control", DataChannel.Init().apply { ordered = true })
        dcInput = peerConnection!!.createDataChannel("dc-input", DataChannel.Init().apply { ordered = true })
        dcClipboard = peerConnection!!.createDataChannel("dc-clipboard", DataChannel.Init().apply { ordered = true })
        dcFile = peerConnection!!.createDataChannel("dc-file", DataChannel.Init().apply { ordered = true })

        dataChannelManager = DataChannelManager().also { mgr ->
            mgr.onChannelOpen = { _dataChannelOpen.tryEmit(Unit) }
            dcControl?.let { mgr.attachChannel("dc-control", it) }
            dcInput?.let { mgr.attachChannel("dc-input", it) }
            dcClipboard?.let { mgr.attachChannel("dc-clipboard", it) }
            dcFile?.let { mgr.attachChannel("dc-file", it) }
        }

        Log.i(TAG, "PeerConnection recreated с fresh ICE config (servers=${iceServers.size})")
    }

    // --- PeerConnection.Observer ---

    private val peerConnectionObserver = object : PeerConnection.Observer {
        override fun onIceCandidate(candidate: IceCandidate) {
            Log.d(TAG, "Local ICE candidate: ${candidate.sdp.take(60)}...")
            _localIceCandidates.tryEmit(candidate)
        }

        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState) {
            Log.d(TAG, "ICE connection state: $state")
            _iceState.value = state
        }

        override fun onTrack(transceiver: RtpTransceiver) {
            val track = transceiver.receiver.track()
            if (track is VideoTrack) {
                Log.d(TAG, "Remote video track received")
                _remoteVideoTrack.value = track
            }
        }

        override fun onSignalingChange(state: PeerConnection.SignalingState) {
            Log.d(TAG, "Signaling state: $state")
        }

        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState) {}
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) {}
        override fun onAddStream(stream: MediaStream) {}
        override fun onRemoveStream(stream: MediaStream) {}
        override fun onDataChannel(channel: DataChannel) {
            val label = channel.label()
            Log.d(TAG, "Remote data channel received: $label — registering for RECEIVE")
            // Host creates its own channels with the same labels.
            // Messages from host arrive on THESE remote channels, not our locally-created ones.
            // Register observer to receive host messages. Keep local channels for sending.
            dataChannelManager?.attachReceiveChannel(label, channel)
        }
        override fun onRenegotiationNeeded() {}
        override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) {}
        override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
            Log.d(TAG, "PeerConnection state: $newState")
        }
    }
}
