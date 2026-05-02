package com.zconect.viewer.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * On-screen keyboard bar with modifier keys, F-keys, Ctrl+Alt+Del,
 * and a button to open the system keyboard for text input.
 */
@Composable
fun KeyboardBar(
    onVirtualKey: (vk: Int, ctrl: Boolean, alt: Boolean, shift: Boolean, win: Boolean) -> Unit,
    onCtrlAltDel: () -> Unit,
    onShowSystemKeyboard: () -> Unit = {},
    modifier: Modifier = Modifier
) {
    var ctrlActive by remember { mutableStateOf(false) }
    var altActive by remember { mutableStateOf(false) }
    var shiftActive by remember { mutableStateOf(false) }
    var winActive by remember { mutableStateOf(false) }

    fun sendKey(vk: Int) {
        onVirtualKey(vk, ctrlActive, altActive, shiftActive, winActive)
        ctrlActive = false
        altActive = false
        shiftActive = false
        winActive = false
    }

    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.95f))
            .padding(4.dp)
    ) {
        // Row 1: System keyboard + Modifier keys + special keys
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            // System keyboard button (prominent)
            Button(
                onClick = onShowSystemKeyboard,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.primary),
                contentPadding = PaddingValues(horizontal = 10.dp, vertical = 4.dp),
                modifier = Modifier.height(36.dp)
            ) {
                Icon(Icons.Default.Keyboard, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(modifier = Modifier.width(4.dp))
                Text("ABC", fontSize = 11.sp)
            }

            ToggleKeyButton("Esc", false, { sendKey(0x1B) })
            ToggleKeyButton("Tab", false, { sendKey(0x09) })
            ToggleKeyButton("Ctrl", ctrlActive, { ctrlActive = !ctrlActive })
            ToggleKeyButton("Alt", altActive, { altActive = !altActive })
            ToggleKeyButton("Shift", shiftActive, { shiftActive = !shiftActive })
            ToggleKeyButton("Win", winActive, { winActive = !winActive })
            ToggleKeyButton("Del", false, { sendKey(0x2E) })
            ToggleKeyButton("Ins", false, { sendKey(0x2D) })
            ToggleKeyButton("Home", false, { sendKey(0x24) })
            ToggleKeyButton("End", false, { sendKey(0x23) })
            ToggleKeyButton("PgUp", false, { sendKey(0x21) })
            ToggleKeyButton("PgDn", false, { sendKey(0x22) })
        }

        Spacer(modifier = Modifier.height(4.dp))

        // Row 2: Arrow keys + F-keys + Ctrl+Alt+Del
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            // Arrow keys
            ToggleKeyButton("←", false, { sendKey(0x25) })
            ToggleKeyButton("↑", false, { sendKey(0x26) })
            ToggleKeyButton("↓", false, { sendKey(0x28) })
            ToggleKeyButton("→", false, { sendKey(0x27) })

            Spacer(modifier = Modifier.width(4.dp))

            for (i in 1..12) {
                ToggleKeyButton("F$i", false, { sendKey(0x6F + i) })
            }

            Spacer(modifier = Modifier.width(4.dp))

            Button(
                onClick = onCtrlAltDel,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                contentPadding = PaddingValues(horizontal = 8.dp, vertical = 4.dp),
                modifier = Modifier.height(36.dp)
            ) {
                Text("C+A+D", fontSize = 11.sp)
            }
        }
    }
}

@Composable
private fun ToggleKeyButton(
    label: String,
    active: Boolean,
    onClick: () -> Unit
) {
    val bgColor = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val textColor = if (active) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface

    Button(
        onClick = onClick,
        colors = ButtonDefaults.buttonColors(containerColor = bgColor, contentColor = textColor),
        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 4.dp),
        modifier = Modifier.height(36.dp)
    ) {
        Text(label, fontSize = 11.sp, textAlign = TextAlign.Center)
    }
}
