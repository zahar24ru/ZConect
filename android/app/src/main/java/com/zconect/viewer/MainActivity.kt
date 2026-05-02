package com.zconect.viewer

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.*
import com.zconect.viewer.data.AppSettings
import com.zconect.viewer.data.RecentHistory
import com.zconect.viewer.data.SessionApi
import com.zconect.viewer.ui.AddressBookScreen
import com.zconect.viewer.ui.ConnectScreen
import com.zconect.viewer.ui.RemoteScreen
import com.zconect.viewer.ui.SettingsScreen
import com.zconect.viewer.ui.theme.ZConectTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            ZConectTheme {
                AppContent()
            }
        }
    }
}

private enum class Screen { Connect, Settings, AddressBook, Remote }

@Composable
private fun AppContent() {
    var screen by remember { mutableStateOf(Screen.Connect) }
    var sessionInfo by remember { mutableStateOf<SessionInfo?>(null) }
    var quickConnectError by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val context = androidx.compose.ui.platform.LocalContext.current
    val settings = remember { AppSettings.getInstance(context) }
    val recentHistory = remember { RecentHistory.getInstance(context) }

    /** Записать успешное подключение в Recent history (или update lastConnected если saved contact). */
    fun recordRecent(loginCode: String, passCode: String) {
        recentHistory.record(
            loginCode = loginCode,
            passCode = passCode,
            serverUrl = settings.serverUrl,
            context = context,
        )
    }

    when (screen) {
        Screen.Connect -> ConnectScreen(
            onConnected = { sessionId, wsUrl, wsToken, loginCode, passCode, turnServers ->
                // contactId resolution: если loginCode совпадает с saved Contact —
                // используем его id для saved-password fast-path. Иначе null (ad-hoc).
                val savedContact = com.zconect.viewer.data.AddressBook.getInstance(context)
                    .getAll().firstOrNull { it.loginCode == loginCode }
                sessionInfo = SessionInfo(sessionId, wsUrl, wsToken, turnServers, savedContact?.id)
                recordRecent(loginCode, passCode)
                screen = Screen.Remote
            },
            onOpenSettings = { screen = Screen.Settings },
            onOpenAddressBook = { screen = Screen.AddressBook }
        )

        Screen.Settings -> SettingsScreen(
            onBack = { screen = Screen.Connect }
        )

        Screen.AddressBook -> AddressBookScreen(
            onBack = { screen = Screen.Connect },
            onConnect = { contact ->
                // Fallback to settings.serverUrl when contact has no explicit server.
                // contact.serverUrl может быть "host:port" (legacy) или "https://host" (новый формат).
                val rawServer = contact.serverUrl.ifBlank { settings.serverUrl }
                val httpBase = normalizeHttpBase(rawServer)
                val wsBase = normalizeWsBase(rawServer)
                settings.loginCode = contact.loginCode
                settings.passCode = contact.passCode

                val api = SessionApi(httpBase)
                scope.launch {
                    try {
                        val response = api.joinSession(contact.loginCode, contact.passCode)
                        sessionInfo = SessionInfo(
                            sessionId = response.sessionId,
                            wsUrl = "$wsBase${response.wsUrl}",
                            wsToken = response.wsToken,
                            turnServers = response.turnServers,
                            // Direct connect через AddressBook — известный contact.
                            contactId = contact.id,
                        )
                        recordRecent(contact.loginCode, contact.passCode)
                        screen = Screen.Remote
                    } catch (e: Exception) {
                        quickConnectError = e.message ?: "Connection failed"
                    }
                }
            }
        )

        Screen.Remote -> {
            val info = sessionInfo
            if (info != null) {
                RemoteScreen(
                    sessionId = info.sessionId,
                    wsUrl = info.wsUrl,
                    wsToken = info.wsToken,
                    initialTurnServers = info.turnServers,
                    contactId = info.contactId,
                    onDisconnected = {
                        sessionInfo = null
                        screen = Screen.Connect
                    }
                )
            }
        }
    }
}

private data class SessionInfo(
    val sessionId: String,
    val wsUrl: String,
    val wsToken: String,
    /** Rotating TURN creds (RFC 7635) из session response. Null = server в legacy mode. */
    val turnServers: List<com.zconect.viewer.data.TurnServerConfig>? = null,
    /** Contact ID для saved-password fast-path (null = ad-hoc connect). */
    val contactId: String? = null,
)

/**
 * Normalize serverUrl → HTTP base URL (for SessionApi/PresenceService).
 *
 * Accepts three input forms:
 *   1. "host:port"           → "http://host:port"   (legacy LAN mode)
 *   2. "http://host[:port]"  → returned as-is
 *   3. "https://host[:port]" → returned as-is       (HTTPS production via Caddy)
 *
 * Trailing '/' stripped. Called from places that hold raw serverUrl (e.g. Contact.serverUrl).
 */
internal fun normalizeHttpBase(rawServer: String): String {
    val s = rawServer.trim().trimEnd('/')
    return when {
        s.startsWith("https://", ignoreCase = true) -> s
        s.startsWith("http://", ignoreCase = true) -> s
        else -> "http://$s"
    }
}

/**
 * Normalize serverUrl → WebSocket base URL (for /ws connections).
 * Matches scheme: https → wss, everything else → ws.
 */
internal fun normalizeWsBase(rawServer: String): String {
    val s = rawServer.trim().trimEnd('/')
    return when {
        s.startsWith("https://", ignoreCase = true) -> "wss://" + s.substring("https://".length)
        s.startsWith("http://", ignoreCase = true) -> "ws://" + s.substring("http://".length)
        else -> "ws://$s"
    }
}
