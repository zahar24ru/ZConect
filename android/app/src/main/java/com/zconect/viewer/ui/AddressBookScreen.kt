package com.zconect.viewer.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.zconect.viewer.data.AddressBook
import com.zconect.viewer.data.Contact
import com.zconect.viewer.data.AppSettings
import com.zconect.viewer.data.PresenceService
import com.zconect.viewer.data.PresenceState
import com.zconect.viewer.R

private enum class SortMode(val labelRes: Int) {
    LAST_CONNECTED(R.string.ab_sort_by_time),
    NAME(R.string.ab_sort_by_name),
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddressBookScreen(
    onBack: () -> Unit,
    onConnect: (Contact) -> Unit
) {
    val context = LocalContext.current
    val settings = remember { AppSettings.getInstance(context) }
    val book = remember { AddressBook.getInstance(context) }
    var contacts by remember { mutableStateOf(book.getAll()) }
    var editing by remember { mutableStateOf<Contact?>(null) }
    var showAddDialog by remember { mutableStateOf(false) }
    var deleteTarget by remember { mutableStateOf<Contact?>(null) }
    var searchQuery by remember { mutableStateOf("") }
    var sortMode by remember { mutableStateOf(SortMode.LAST_CONNECTED) }
    var showSortMenu by remember { mutableStateOf(false) }

    val haptic = LocalHapticFeedback.current

    // Presence polling — same server, all contact login codes. Refresh при
    // каждом contacts change (add/delete).
    val presenceService = remember {
        PresenceService(serverBaseUrl = settings.httpBaseUrl)
    }
    DisposableEffect(Unit) {
        onDispose { presenceService.dispose() }
    }
    LaunchedEffect(contacts) {
        presenceService.setLogins(contacts.map { it.loginCode })
    }
    // Audit fix 2026-04-27: collectAsStateWithLifecycle вместо collectAsState —
    // pause polling когда screen в background → battery + network saved.
    val presenceMap by presenceService.presenceMap.collectAsStateWithLifecycle()

    // Filter by search + sort
    val filtered = remember(contacts, searchQuery, sortMode) {
        val q = searchQuery.trim().lowercase()
        contacts
            .filter { q.isEmpty() || it.name.lowercase().contains(q) || it.loginCode.contains(q) }
            .let { list ->
                when (sortMode) {
                    SortMode.LAST_CONNECTED -> list.sortedByDescending { it.lastConnectedUtc }
                    SortMode.NAME -> list.sortedBy { it.name.lowercase() }
                }
            }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.ab_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.common_back))
                    }
                },
                actions = {
                    // Sort menu
                    Box {
                        IconButton(onClick = { showSortMenu = true }) {
                            Icon(Icons.AutoMirrored.Filled.Sort, contentDescription = stringResource(R.string.ab_sort_label))
                        }
                        DropdownMenu(
                            expanded = showSortMenu,
                            onDismissRequest = { showSortMenu = false }
                        ) {
                            SortMode.entries.forEach { mode ->
                                DropdownMenuItem(
                                    text = { Text(stringResource(mode.labelRes)) },
                                    onClick = {
                                        sortMode = mode
                                        showSortMenu = false
                                    },
                                    trailingIcon = {
                                        if (mode == sortMode) {
                                            Icon(
                                                Icons.Default.PlayArrow,
                                                null,
                                                modifier = Modifier.size(16.dp)
                                            )
                                        }
                                    }
                                )
                            }
                        }
                    }
                }
            )
        },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    showAddDialog = true
                },
                icon = { Icon(Icons.Default.Add, contentDescription = null) },
                text = { Text(stringResource(R.string.ab_add_button)) }
            )
        }
    ) { padding ->
        Column(modifier = Modifier.fillMaxSize().padding(padding)) {
            // ─── Search bar ───────────────────────────────────────
            if (contacts.isNotEmpty()) {
                OutlinedTextField(
                    value = searchQuery,
                    onValueChange = { searchQuery = it },
                    placeholder = { Text(stringResource(R.string.ab_search_placeholder)) },
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                    trailingIcon = {
                        if (searchQuery.isNotEmpty()) {
                            IconButton(onClick = { searchQuery = "" }) {
                                Icon(Icons.Default.Clear, contentDescription = stringResource(R.string.common_clear))
                            }
                        }
                    },
                    singleLine = true,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                )
            }

            // ─── Content ────────────────────────────────────────
            if (contacts.isEmpty()) {
                EmptyState()
            } else if (filtered.isEmpty()) {
                // Search нашёл нуль — different message
                Column(
                    modifier = Modifier.fillMaxSize().padding(32.dp),
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Icon(
                        Icons.Default.Search,
                        null,
                        modifier = Modifier.size(48.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Spacer(modifier = Modifier.height(12.dp))
                    Text(
                        stringResource(R.string.ab_search_no_results),
                        style = MaterialTheme.typography.titleMedium,
                    )
                }
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    items(filtered, key = { it.id }) { contact ->
                        ContactCard(
                            contact = contact,
                            presence = presenceMap[contact.loginCode] ?: PresenceState.UNKNOWN,
                            onConnect = {
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                onConnect(contact)
                            },
                            onEdit = { editing = contact },
                            onDelete = {
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                deleteTarget = contact
                            }
                        )
                    }
                    item { Spacer(modifier = Modifier.height(80.dp)) } // FAB padding
                }
            }
        }
    }

    if (showAddDialog) {
        ContactEditorDialog(
            contact = null,
            onDismiss = { showAddDialog = false },
            onSave = { c ->
                book.add(c)
                contacts = book.getAll()
                showAddDialog = false
            }
        )
    }

    editing?.let { c ->
        ContactEditorDialog(
            contact = c,
            onDismiss = { editing = null },
            onSave = { updated ->
                book.update(updated)
                contacts = book.getAll()
                editing = null
            }
        )
    }

    deleteTarget?.let { c ->
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text(stringResource(R.string.ab_delete_dialog_title_format, c.name)) },
            text = { Text(stringResource(R.string.ab_delete_dialog_message)) },
            confirmButton = {
                TextButton(onClick = {
                    book.remove(c.id)
                    // Audit fix #4 2026-04-27: cleanup saved password из SecurePrefs
                    // когда contact удаляется. Иначе orphan keys накапливаются + если
                    // user пере-добавит contact с тем же id (unlikely — UUID), увидит
                    // stale password. Defensive cleanup.
                    com.zconect.viewer.data.SecurePrefs.getInstance(context)
                        .removeContactPassword(c.id)
                    contacts = book.getAll()
                    deleteTarget = null
                }) { Text(stringResource(R.string.common_delete), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(onClick = { deleteTarget = null }) { Text(stringResource(R.string.common_cancel)) }
            }
        )
    }
}

@Composable
private fun EmptyState() {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            Icons.Default.Computer,
            contentDescription = null,
            modifier = Modifier.size(72.dp),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(modifier = Modifier.height(16.dp))
        Text(
            stringResource(R.string.ab_empty_title),
            style = MaterialTheme.typography.titleMedium,
        )
        Spacer(modifier = Modifier.height(4.dp))
        Text(
            stringResource(R.string.ab_empty_subtitle),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun ContactCard(
    contact: Contact,
    presence: PresenceState,
    onConnect: () -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit
) {
    val firstLetter = if (contact.name.isBlank()) "?"
        else contact.name.trim().substring(0, 1).uppercase()

    Card(
        onClick = onConnect,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            ContactAvatar(
                name = contact.name,
                firstLetter = firstLetter,
                size = 44,
                presence = presence,
                isSaved = true,
            )
            Spacer(modifier = Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = contact.name,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                val subtitle = buildString {
                    append("Login: ${contact.loginCode.take(4)}••••")
                    if (contact.lastConnectedUtc > 0) {
                        append(" · ")
                        append(formatRelativeTime(contact.lastConnectedUtc))
                    }
                }
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            IconButton(onClick = onEdit) {
                Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.common_edit))
            }
            IconButton(onClick = onDelete) {
                Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.common_delete), tint = MaterialTheme.colorScheme.error)
            }
        }
    }
}

@Composable
private fun ContactEditorDialog(
    contact: Contact?,
    onDismiss: () -> Unit,
    onSave: (Contact) -> Unit
) {
    var name by remember { mutableStateOf(contact?.name ?: "") }
    var loginCode by remember { mutableStateOf(contact?.loginCode ?: "") }
    var passCode by remember { mutableStateOf(contact?.passCode ?: "") }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(
            if (contact == null) R.string.ab_dialog_add_title
            else R.string.ab_dialog_edit_title
        )) },
        text = {
            Column {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text(stringResource(R.string.ab_name_label)) },
                    placeholder = { Text(stringResource(R.string.ab_name_placeholder)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(modifier = Modifier.height(8.dp))
                OutlinedTextField(
                    value = loginCode,
                    onValueChange = { if (it.length <= 8 && it.all { c -> c.isDigit() }) loginCode = it },
                    label = { Text(stringResource(R.string.connect_login_label)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(modifier = Modifier.height(8.dp))
                OutlinedTextField(
                    value = passCode,
                    onValueChange = { if (it.length <= 8 && it.all { c -> c.isDigit() }) passCode = it },
                    label = { Text(stringResource(R.string.connect_pass_label)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = {
                    // serverUrl left empty — MainActivity falls back to settings.serverUrl on connect.
                    val saved = (contact ?: Contact(name = "", serverUrl = "", loginCode = "", passCode = ""))
                        .copy(name = name.trim(), serverUrl = "", loginCode = loginCode, passCode = passCode)
                    onSave(saved)
                },
                enabled = name.isNotBlank() && loginCode.length == 8 && passCode.length == 8
            ) { Text(stringResource(R.string.common_save)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
        }
    )
}
