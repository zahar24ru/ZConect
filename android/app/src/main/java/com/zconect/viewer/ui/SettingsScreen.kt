package com.zconect.viewer.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.zconect.viewer.R
import com.zconect.viewer.data.AppSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val ctxRes = context // для resolve внутри coroutine
    val settings = remember { AppSettings.getInstance(context) }
    val scope = rememberCoroutineScope()

    // Local state for editing
    var serverUrl by remember { mutableStateOf(settings.serverUrl) }
    var stunUrl by remember { mutableStateOf(settings.stunUrl) }
    var turnUrl by remember { mutableStateOf(settings.turnUrl) }
    var turnUser by remember { mutableStateOf(settings.turnUser) }
    var turnPass by remember { mutableStateOf(settings.turnPass) }
    var hostScreenW by remember { mutableStateOf(settings.hostScreenW.toString()) }
    var hostScreenH by remember { mutableStateOf(settings.hostScreenH.toString()) }
    var trackpadSpeed by remember { mutableStateOf(settings.trackpadSpeed) }
    var scrollSpeed by remember { mutableStateOf(settings.scrollSpeed) }
    var autoReconnect by remember { mutableStateOf(settings.autoReconnect) }

    // Connection test state
    var testResult by remember { mutableStateOf<Boolean?>(null) }
    var testMessage by remember { mutableStateOf("") }
    var isTesting by remember { mutableStateOf(false) }

    // Auto-save on change
    fun save() {
        settings.serverUrl = serverUrl
        settings.stunUrl = stunUrl
        settings.turnUrl = turnUrl
        settings.turnUser = turnUser
        settings.turnPass = turnPass
        settings.hostScreenW = hostScreenW.toIntOrNull() ?: AppSettings.DEFAULT_HOST_SCREEN_W
        settings.hostScreenH = hostScreenH.toIntOrNull() ?: AppSettings.DEFAULT_HOST_SCREEN_H
        settings.trackpadSpeed = trackpadSpeed
        settings.scrollSpeed = scrollSpeed
        settings.autoReconnect = autoReconnect
    }

    Scaffold(
        topBar = {
            LargeTopAppBar(
                title = { Text(stringResource(R.string.settings_title)) },
                navigationIcon = {
                    IconButton(onClick = { save(); onBack() }) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.common_back))
                    }
                },
                colors = TopAppBarDefaults.largeTopAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    titleContentColor = MaterialTheme.colorScheme.onSurface
                )
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp)
        ) {
            // ═══════════════════════════════════════
            // Server section
            // ═══════════════════════════════════════
            SectionHeader(stringResource(R.string.settings_section_server))

            OutlinedTextField(
                value = serverUrl,
                onValueChange = { serverUrl = it; save() },
                label = { Text(stringResource(R.string.settings_server_label)) },
                placeholder = { Text(stringResource(R.string.settings_server_placeholder)) },
                supportingText = { Text(stringResource(R.string.settings_server_help), style = MaterialTheme.typography.labelSmall) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(modifier = Modifier.height(12.dp))

            // Test Connection button
            val testColor by animateColorAsState(
                targetValue = when (testResult) {
                    true -> Color(0xFF4CAF50)
                    false -> MaterialTheme.colorScheme.error
                    null -> MaterialTheme.colorScheme.primary
                },
                label = "testColor"
            )

            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                FilledTonalButton(
                    onClick = {
                        isTesting = true
                        testResult = null
                        testMessage = ""
                        save()
                        scope.launch {
                            try {
                                // Auto-prefix scheme: https:// stays, иначе http://
                                val normalized = when {
                                    serverUrl.startsWith("https://", ignoreCase = true) -> serverUrl.trimEnd('/')
                                    serverUrl.startsWith("http://", ignoreCase = true) -> serverUrl.trimEnd('/')
                                    else -> "http://${serverUrl.trimEnd('/')}"
                                }
                                val result = testConnection(normalized, ctxRes)
                                testResult = result.first
                                testMessage = result.second
                            } catch (e: Exception) {
                                testResult = false
                                testMessage = e.message ?: ctxRes.getString(R.string.common_unknown_error)
                            }
                            isTesting = false
                        }
                    },
                    enabled = !isTesting && serverUrl.isNotBlank(),
                    colors = ButtonDefaults.filledTonalButtonColors(containerColor = testColor.copy(alpha = 0.15f)),
                    modifier = Modifier.weight(1f)
                ) {
                    if (isTesting) {
                        CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                    } else {
                        when (testResult) {
                            true -> Icon(Icons.Default.Check, contentDescription = null, tint = testColor, modifier = Modifier.size(18.dp))
                            false -> Icon(Icons.Default.Close, contentDescription = null, tint = testColor, modifier = Modifier.size(18.dp))
                            null -> {}
                        }
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(stringResource(
                        when {
                            isTesting -> R.string.settings_test_running
                            testResult == true -> R.string.settings_test_ok
                            testResult == false -> R.string.settings_test_failed
                            else -> R.string.settings_test_default
                        }
                    ))
                }
            }

            if (testMessage.isNotBlank()) {
                Spacer(modifier = Modifier.height(4.dp))
                Text(
                    text = testMessage,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (testResult == true) Color(0xFF4CAF50) else MaterialTheme.colorScheme.error
                )
            }

            Spacer(modifier = Modifier.height(24.dp))

            // ═══════════════════════════════════════
            // WebRTC section
            // ═══════════════════════════════════════
            SectionHeader(stringResource(R.string.settings_section_webrtc))

            OutlinedTextField(
                value = stunUrl,
                onValueChange = { stunUrl = it; save() },
                label = { Text(stringResource(R.string.settings_stun_label)) },
                placeholder = { Text(stringResource(R.string.settings_stun_placeholder)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(modifier = Modifier.height(8.dp))

            OutlinedTextField(
                value = turnUrl,
                onValueChange = { turnUrl = it; save() },
                label = { Text(stringResource(R.string.settings_turn_label)) },
                placeholder = { Text(stringResource(R.string.settings_turn_placeholder)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(modifier = Modifier.height(8.dp))

            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = turnUser,
                    onValueChange = { turnUser = it; save() },
                    label = { Text(stringResource(R.string.settings_turn_user_label)) },
                    singleLine = true,
                    modifier = Modifier.weight(1f)
                )
                OutlinedTextField(
                    value = turnPass,
                    onValueChange = { turnPass = it; save() },
                    label = { Text(stringResource(R.string.settings_turn_pass_label)) },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.weight(1f)
                )
            }

            Spacer(modifier = Modifier.height(24.dp))

            // ═══════════════════════════════════════
            // Display section
            // ═══════════════════════════════════════
            SectionHeader(stringResource(R.string.settings_section_display))

            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = hostScreenW,
                    onValueChange = { if (it.all { c -> c.isDigit() }) { hostScreenW = it; save() } },
                    label = { Text(stringResource(R.string.settings_display_width)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f)
                )
                Text("x", modifier = Modifier.align(Alignment.CenterVertically), style = MaterialTheme.typography.titleMedium)
                OutlinedTextField(
                    value = hostScreenH,
                    onValueChange = { if (it.all { c -> c.isDigit() }) { hostScreenH = it; save() } },
                    label = { Text(stringResource(R.string.settings_display_height)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f)
                )
            }

            Spacer(modifier = Modifier.height(24.dp))

            // ═══════════════════════════════════════
            // Input & Behavior
            // ═══════════════════════════════════════
            SectionHeader(stringResource(R.string.settings_section_input))

            // Trackpad speed
            Text(
                text = stringResource(R.string.settings_trackpad_speed_format, trackpadSpeed),
                style = MaterialTheme.typography.bodyMedium
            )
            Slider(
                value = trackpadSpeed,
                onValueChange = { trackpadSpeed = it; save() },
                valueRange = 0.5f..4.0f,
                steps = 34 // 0.1 increments
            )

            Spacer(modifier = Modifier.height(8.dp))

            // Scroll speed
            Text(
                text = stringResource(R.string.settings_scroll_speed_format, scrollSpeed.toInt()),
                style = MaterialTheme.typography.bodyMedium
            )
            Slider(
                value = scrollSpeed,
                onValueChange = { scrollSpeed = it; save() },
                valueRange = 10f..120f,
                steps = 21 // 5-unit increments
            )

            Spacer(modifier = Modifier.height(12.dp))

            // Auto-reconnect toggle
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(stringResource(R.string.settings_autoreconnect_title), style = MaterialTheme.typography.bodyLarge)
                    Text(
                        stringResource(R.string.settings_autoreconnect_subtitle),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Switch(checked = autoReconnect, onCheckedChange = { autoReconnect = it; save() })
            }

            Spacer(modifier = Modifier.height(32.dp))

            // ═══════════════════════════════════════
            // Reset
            // ═══════════════════════════════════════
            OutlinedButton(
                onClick = {
                    settings.resetToDefaults()
                    serverUrl = settings.serverUrl
                    stunUrl = settings.stunUrl
                    turnUrl = settings.turnUrl
                    turnUser = settings.turnUser
                    turnPass = settings.turnPass
                    hostScreenW = settings.hostScreenW.toString()
                    hostScreenH = settings.hostScreenH.toString()
                    trackpadSpeed = settings.trackpadSpeed
                    scrollSpeed = settings.scrollSpeed
                    autoReconnect = settings.autoReconnect
                    testResult = null
                    testMessage = ""
                },
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(modifier = Modifier.width(8.dp))
                Text(stringResource(R.string.settings_reset_button))
            }

            Spacer(modifier = Modifier.height(24.dp))
        }
    }
}

@Composable
private fun SectionHeader(title: String) {
    Text(
        text = title,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(bottom = 8.dp, top = 4.dp)
    )
}

/**
 * Test server connectivity by calling GET /healthz.
 * Returns (success, message).
 */
private suspend fun testConnection(baseUrl: String, ctx: android.content.Context): Pair<Boolean, String> =
    withContext(Dispatchers.IO) {
        try {
            val url = URL("$baseUrl/healthz")
            val connection = url.openConnection() as HttpURLConnection
            connection.connectTimeout = 5000
            connection.readTimeout = 5000
            connection.requestMethod = "GET"

            val code = connection.responseCode
            val body = try { connection.inputStream.bufferedReader().readText() } catch (_: Exception) { "" }
            connection.disconnect()

            if (code == 200) {
                Pair(true, ctx.getString(R.string.settings_test_server_ok_format, code))
            } else {
                Pair(false, ctx.getString(R.string.settings_test_http_format, code))
            }
        } catch (e: java.net.ConnectException) {
            Pair(false, ctx.getString(R.string.settings_test_refused))
        } catch (e: java.net.SocketTimeoutException) {
            Pair(false, ctx.getString(R.string.settings_test_timeout))
        } catch (e: Exception) {
            Pair(false, e.message ?: ctx.getString(R.string.common_unknown_error))
        }
    }
