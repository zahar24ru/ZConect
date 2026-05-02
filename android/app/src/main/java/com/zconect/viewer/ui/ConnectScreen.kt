package com.zconect.viewer.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.PersonAdd
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.zconect.viewer.data.AddressBook
import com.zconect.viewer.data.AppSettings
import com.zconect.viewer.data.Contact
import com.zconect.viewer.data.PresenceService
import com.zconect.viewer.data.PresenceState
import com.zconect.viewer.data.RecentHistory
import com.zconect.viewer.data.RecentItem
import com.zconect.viewer.data.SessionApi
import com.zconect.viewer.R
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectScreen(
    onConnected: (sessionId: String, wsUrl: String, wsToken: String, loginCode: String, passCode: String, turnServers: List<com.zconect.viewer.data.TurnServerConfig>?) -> Unit,
    onOpenSettings: () -> Unit,
    onOpenAddressBook: () -> Unit
) {
    val context = LocalContext.current
    // ctxRes — для использования в catch-блоках (suspend Lambda не может
    // вызвать stringResource, нужен resolver через context).
    val ctxRes = context
    val settings = remember { AppSettings.getInstance(context) }
    val addressBook = remember { AddressBook.getInstance(context) }
    val recentHistory = remember { RecentHistory.getInstance(context) }

    // Рефреш data when screen resumed (AddressBook могли поменять на другом экране).
    var refreshTick by remember { mutableStateOf(0) }
    val contacts = remember(refreshTick) { addressBook.getAll() }
    val history = remember(refreshTick) { recentHistory.getAll() }
    val recentItems = remember(contacts, history) {
        RecentItem.buildRecentList(contacts, history, topN = 5)
    }

    // Presence polling. Shared service — setLogins() каждый раз когда recentItems меняется.
    val presenceService = remember {
        PresenceService(serverBaseUrl = settings.httpBaseUrl)
    }
    DisposableEffect(Unit) {
        onDispose { presenceService.dispose() }
    }
    LaunchedEffect(recentItems) {
        presenceService.setLogins(recentItems.map { it.loginCode })
    }
    // Audit fix 2026-04-27: collectAsStateWithLifecycle вместо collectAsState —
    // pause collection когда screen в background → battery + network saved.
    val presenceMap by presenceService.presenceMap.collectAsStateWithLifecycle()

    var loginCode by remember { mutableStateOf(settings.loginCode) }
    var passCode by remember { mutableStateOf(settings.passCode) }
    var isConnecting by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    // Save-to-contacts dialog для ephemeral Recent items
    var saveToContactsTarget by remember { mutableStateOf<RecentItem?>(null) }

    val scope = rememberCoroutineScope()

    /** Выполнить подключение с данным login/pass. Общая логика для ручного input и Recent click. */
    fun attemptConnect(login: String, pass: String) {
        isConnecting = true
        errorMessage = null
        val api = SessionApi(settings.httpBaseUrl)
        scope.launch {
            try {
                val response = api.joinSession(login, pass)
                isConnecting = false
                onConnected(
                    response.sessionId,
                    "${settings.wsBaseUrl}${response.wsUrl}",
                    response.wsToken,
                    login,
                    pass,
                    response.turnServers,
                )
            } catch (e: SessionApi.ApiException.InvalidCredentials) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_invalid_credentials)
            } catch (e: SessionApi.ApiException.Banned) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_banned)
            } catch (e: SessionApi.ApiException.SessionBlocked) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_session_blocked)
            } catch (e: SessionApi.ApiException.SessionLocked) {
                isConnecting = false
                errorMessage = if (e.retryAfterSec > 0)
                    ctxRes.getString(R.string.error_session_locked_format, e.retryAfterSec)
                else ctxRes.getString(R.string.error_session_locked_unspecified)
            } catch (e: SessionApi.ApiException.Maintenance) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_maintenance)
            } catch (e: SessionApi.ApiException.NetworkError) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_network)
            } catch (e: SessionApi.ApiException) {
                isConnecting = false
                errorMessage = e.message
            } catch (e: Exception) {
                isConnecting = false
                errorMessage = ctxRes.getString(R.string.error_generic_format, e.message ?: "")
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.connect_title)) },
                actions = {
                    IconButton(onClick = onOpenAddressBook) {
                        Icon(Icons.AutoMirrored.Filled.MenuBook, contentDescription = stringResource(R.string.connect_open_address_book))
                    }
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Default.Settings, contentDescription = stringResource(R.string.connect_open_settings))
                    }
                }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = 20.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.Top,
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // ─── Server info (read-only, from settings) ────────────────────
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = stringResource(R.string.connect_server_format, settings.serverUrl),
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.weight(1f),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    TextButton(onClick = onOpenSettings) { Text(stringResource(R.string.common_change)) }
                }
            }

            // ─── Recent connections row (top of screen for quick-access) ─────
            if (recentItems.isNotEmpty()) {
                Spacer(modifier = Modifier.height(20.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.connect_recent_title),
                        style = MaterialTheme.typography.labelLarge,
                        fontWeight = FontWeight.SemiBold,
                    )
                    TextButton(onClick = onOpenAddressBook) {
                        Text(stringResource(R.string.connect_to_address_book), style = MaterialTheme.typography.labelMedium)
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
                LazyRow(
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    items(recentItems, key = { it.loginCode }) { item ->
                        RecentCard(
                            item = item,
                            presence = presenceMap[item.loginCode] ?: PresenceState.UNKNOWN,
                            onClick = {
                                if (!isConnecting) attemptConnect(item.loginCode, item.passCode)
                            },
                            onSaveToContacts = if (!item.isSaved) {
                                { saveToContactsTarget = item }
                            } else null,
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(28.dp))

            // ─── Login / Pass input ──────────────────────────────────────────
            OutlinedTextField(
                value = loginCode,
                onValueChange = {
                    if (it.length <= 8 && it.all { c -> c.isDigit() }) {
                        loginCode = it
                        settings.loginCode = it
                    }
                },
                label = { Text(stringResource(R.string.connect_login_label)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(modifier = Modifier.height(12.dp))

            OutlinedTextField(
                value = passCode,
                onValueChange = {
                    if (it.length <= 8 && it.all { c -> c.isDigit() }) {
                        passCode = it
                        settings.passCode = it
                    }
                },
                label = { Text(stringResource(R.string.connect_pass_label)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(modifier = Modifier.height(20.dp))

            Button(
                onClick = { attemptConnect(loginCode, passCode) },
                enabled = !isConnecting && loginCode.length == 8 && passCode.length == 8,
                modifier = Modifier.fillMaxWidth()
            ) {
                if (isConnecting) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(20.dp),
                        strokeWidth = 2.dp,
                        color = MaterialTheme.colorScheme.onPrimary
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                }
                Text(stringResource(
                    if (isConnecting) R.string.connect_button_in_progress
                    else R.string.connect_button
                ))
            }

            errorMessage?.let { error ->
                Spacer(modifier = Modifier.height(16.dp))
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    )
                ) {
                    Text(
                        text = error,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(12.dp),
                        textAlign = TextAlign.Center,
                    )
                }
            }
        }
    }

    // Save-to-contacts dialog
    saveToContactsTarget?.let { target ->
        SaveToContactsDialog(
            item = target,
            onDismiss = { saveToContactsTarget = null },
            onSave = { name ->
                val newContact = Contact(
                    name = name.trim(),
                    serverUrl = "", // falls back to settings.serverUrl
                    loginCode = target.loginCode,
                    passCode = target.passCode,
                    lastConnectedUtc = target.lastConnectedUtc,
                )
                val book = AddressBook.getInstance(context)
                book.add(newContact)
                // Удаляем из history — теперь это saved contact
                RecentHistory.getInstance(context).remove(target.loginCode)
                refreshTick++ // rebuild recentItems
                saveToContactsTarget = null
            }
        )
    }
}

/**
 * Dialog для сохранения ephemeral Recent item в AddressBook с user-заданным именем.
 * Mirror Windows "Сохранить в адресную книгу" prompt.
 */
@Composable
private fun SaveToContactsDialog(
    item: RecentItem,
    onDismiss: () -> Unit,
    onSave: (name: String) -> Unit,
) {
    var name by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.save_to_contacts_title)) },
        text = {
            Column {
                Text(
                    stringResource(R.string.save_to_contacts_message),
                    style = MaterialTheme.typography.bodyMedium,
                )
                Spacer(modifier = Modifier.height(12.dp))
                OutlinedTextField(
                    value = name,
                    onValueChange = { if (it.length <= 64) name = it },
                    label = { Text(stringResource(R.string.save_to_contacts_name_label)) },
                    placeholder = { Text(stringResource(R.string.save_to_contacts_name_placeholder)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(
                    stringResource(R.string.save_to_contacts_login_format, item.loginCode.take(4)),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onSave(name) },
                enabled = name.trim().isNotBlank(),
            ) { Text(stringResource(R.string.common_save)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
        }
    )
}

/** Compact recent connection card — avatar с presence dot + name + relative time.
 *
 * Для ephemeral items (isSaved=false) card становится выше (160dp вместо 140)
 * и внизу добавляется отдельная полноширинная кнопка «Сохранить» — так user
 * может её 100% попасть пальцем (48dp высота, whole-width touch target).
 *
 * Дополнительно — long-press на любую card тоже открывает save dialog (haptic
 * feedback), как backup если user промахнулся мимо button. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun RecentCard(
    item: RecentItem,
    presence: PresenceState,
    onClick: () -> Unit,
    onSaveToContacts: (() -> Unit)? = null,
) {
    val haptic = LocalHapticFeedback.current
    // Ephemeral card выше чтобы поместить save-button снизу
    val cardHeight = if (onSaveToContacts != null) 172.dp else 140.dp

    Card(
        modifier = Modifier
            .width(110.dp)
            .height(cardHeight)
            .combinedClickable(
                onClick = onClick,
                onLongClick = onSaveToContacts?.let {
                    {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        it()
                    }
                },
            ),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 8.dp, vertical = 10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            ContactAvatar(
                name = item.displayName,
                firstLetter = item.firstLetter,
                size = 44,
                presence = presence,
                isSaved = item.isSaved,
            )
            Spacer(modifier = Modifier.height(6.dp))
            Text(
                text = item.displayName,
                style = MaterialTheme.typography.labelMedium,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
            Text(
                text = formatRelativeTime(item.lastConnectedUtc),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
            // Save-to-contacts button — полноширинная, легко попадается
            onSaveToContacts?.let { handler ->
                Spacer(modifier = Modifier.weight(1f))
                TextButton(
                    onClick = handler,
                    contentPadding = PaddingValues(horizontal = 4.dp, vertical = 4.dp),
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(32.dp),
                ) {
                    Icon(
                        Icons.Default.PersonAdd,
                        contentDescription = null,
                        modifier = Modifier.size(14.dp),
                    )
                    Spacer(modifier = Modifier.width(4.dp))
                    Text(
                        stringResource(R.string.save_to_contacts_button_save_short),
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                    )
                }
            }
        }
    }
}
