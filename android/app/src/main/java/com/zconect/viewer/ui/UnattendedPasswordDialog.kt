package com.zconect.viewer.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.zconect.viewer.R
import com.zconect.viewer.data.UnattendedAuthCoordinator
import kotlinx.coroutines.delay

/**
 * Password input dialog shown во время unattended auth handshake.
 *
 * Two states:
 * - Normal/retry: user enters password, can submit (Connect) or cancel
 *   (Wait for confirmation — fallbacks на host's confirmation dialog).
 * - Locked out: shows lockout duration / tier — submit disabled, only OK
 *   to dismiss. User reconnect'нется когда countdown истечёт (no auto-retry).
 */
@Composable
fun UnattendedPasswordDialog(
    prompt: UnattendedAuthCoordinator.Prompt,
    onSubmit: (password: String, remember: Boolean) -> Unit,
    onCancel: () -> Unit,
) {
    var password by remember { mutableStateOf("") }
    var rememberPassword by remember { mutableStateOf(false) }
    val isLocked = prompt.isLockedOut

    // Live countdown для lockout state (audit fix #5 2026-04-27).
    // Раньше показывали статичное число — user не видел progress unlock'а.
    // Теперь LaunchedEffect tick'ает каждую секунду, decrement до 0.
    // Когда countdown истёк — показывается 0:00, user может закрыть dialog
    // и попробовать reconnect (auto-retry не делаем — explicit user action
    // safer от brute-force).
    var remainingSec by remember(prompt.lockoutRemainingSec, isLocked) {
        mutableStateOf(prompt.lockoutRemainingSec)
    }
    LaunchedEffect(isLocked, prompt.lockoutRemainingSec) {
        if (isLocked) {
            while (remainingSec > 0) {
                delay(1000)
                remainingSec -= 1
            }
        }
    }

    AlertDialog(
        onDismissRequest = onCancel,
        icon = { Icon(Icons.Default.Lock, contentDescription = null) },
        title = { Text(stringResource(R.string.unattended_dialog_title)) },
        text = {
            Column {
                Text(
                    stringResource(R.string.unattended_dialog_message),
                    style = MaterialTheme.typography.bodyMedium,
                )

                if (prompt.isRetry && !isLocked) {
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        stringResource(R.string.unattended_wrong_password_format, prompt.attemptsLeft),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }

                if (isLocked) {
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        stringResource(
                            R.string.unattended_locked_out_format,
                            remainingSec, // live countdown через LaunchedEffect
                            prompt.lockoutTier,
                        ),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                } else {
                    Spacer(modifier = Modifier.height(12.dp))
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text(stringResource(R.string.unattended_dialog_field_label)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                        visualTransformation = PasswordVisualTransformation(),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    // "Remember password" checkbox — only shown if connect к saved
                    // contact (canRemember=true). Audit fix #4 2026-04-27: на следующий
                    // connect viewer auto-sends saved password → silent reconnect.
                    if (prompt.canRemember) {
                        Spacer(modifier = Modifier.height(8.dp))
                        Row(
                            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Checkbox(
                                checked = rememberPassword,
                                onCheckedChange = { rememberPassword = it },
                            )
                            Text(
                                stringResource(R.string.unattended_dialog_remember),
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            if (isLocked) {
                // Lockout state — only OK button to dismiss, no submit possible.
                TextButton(onClick = onCancel) {
                    Text(stringResource(R.string.common_got_it))
                }
            } else {
                TextButton(
                    onClick = { onSubmit(password, rememberPassword) },
                    enabled = password.isNotEmpty(),
                ) {
                    Text(stringResource(R.string.unattended_dialog_submit))
                }
            }
        },
        dismissButton = if (!isLocked) {
            {
                TextButton(onClick = onCancel) {
                    Text(stringResource(R.string.unattended_dialog_cancel))
                }
            }
        } else null,
    )
}
