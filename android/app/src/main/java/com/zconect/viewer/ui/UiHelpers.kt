package com.zconect.viewer.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.zconect.viewer.data.PresenceState

/**
 * UI helpers для Recent/AddressBook — аватары, relative time, presence dots.
 * Mirror Windows client-side UI conventions.
 */

/** 8 gradient pairs для аватаров — hash of name → index. Mirror Windows NameToAvatarBrushConverter. */
private val AVATAR_GRADIENTS = listOf(
    listOf(Color(0xFF6366F1), Color(0xFF8B5CF6)), // indigo → violet
    listOf(Color(0xFFEC4899), Color(0xFFEF4444)), // pink → red
    listOf(Color(0xFF14B8A6), Color(0xFF0EA5E9)), // teal → cyan
    listOf(Color(0xFFF59E0B), Color(0xFFEF4444)), // amber → red
    listOf(Color(0xFF22C55E), Color(0xFF14B8A6)), // green → teal
    listOf(Color(0xFFA855F7), Color(0xFFEC4899)), // purple → pink
    listOf(Color(0xFF3B82F6), Color(0xFF6366F1)), // blue → indigo
    listOf(Color(0xFFF97316), Color(0xFFEF4444)), // orange → red
)

private fun avatarGradient(name: String): Brush {
    val idx = if (name.isBlank()) 0 else Math.floorMod(name.hashCode(), AVATAR_GRADIENTS.size)
    return Brush.linearGradient(AVATAR_GRADIENTS[idx])
}

/**
 * Круглый аватар с gradient фоном + первая буква имени.
 * Presence dot в bottom-right, если state != null.
 */
@Composable
fun ContactAvatar(
    name: String,
    firstLetter: String,
    size: Int = 40,
    presence: PresenceState? = null,
    isSaved: Boolean = true,
) {
    Box(
        modifier = Modifier.size(size.dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .size(size.dp)
                .clip(CircleShape)
                .background(
                    if (isSaved) avatarGradient(name)
                    else Brush.linearGradient(listOf(Color(0xFF9CA3AF), Color(0xFF6B7280)))
                ),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = firstLetter,
                color = Color.White,
                fontWeight = FontWeight.SemiBold,
                fontSize = (size * 0.4).sp,
            )
        }
        // Presence dot (bottom-right corner) — 🟢 online / 🔴 offline / ⚪ unknown
        presence?.let { ps ->
            val dotSize = (size * 0.3).toInt().coerceAtLeast(8)
            val color = when (ps) {
                PresenceState.ONLINE -> Color(0xFF22C55E)  // green
                PresenceState.OFFLINE -> Color(0xFF9CA3AF) // gray
                PresenceState.UNKNOWN -> Color(0xFFD1D5DB) // light gray
            }
            Box(
                modifier = Modifier
                    .size(dotSize.dp)
                    .align(Alignment.BottomEnd)
                    .clip(CircleShape)
                    .background(MaterialTheme.colorScheme.surface), // stroke via bg inset
                contentAlignment = Alignment.Center,
            ) {
                Box(
                    modifier = Modifier
                        .size((dotSize - 2).dp)
                        .clip(CircleShape)
                        .background(color)
                )
            }
        }
    }
}

/**
 * Human-readable relative time — «только что», «5 мин назад», «2 ч назад», «вчера».
 * Mirror Windows Contact.LastConnectedDisplay logic. Локализация — Phase 3 (сейчас русский).
 */
fun formatRelativeTime(epochMillis: Long): String {
    if (epochMillis <= 0) return ""
    val now = System.currentTimeMillis()
    val deltaSec = (now - epochMillis) / 1000
    if (deltaSec < 0) return "только что"
    return when {
        deltaSec < 60 -> "только что"
        deltaSec < 3600 -> "${deltaSec / 60} мин назад"
        deltaSec < 86400 -> "${deltaSec / 3600} ч назад"
        deltaSec < 172800 -> "вчера"
        deltaSec < 604800 -> "${deltaSec / 86400} дн назад"
        deltaSec < 2592000 -> "${deltaSec / 604800} нед назад"
        else -> "${deltaSec / 2592000} мес назад"
    }
}

fun presenceTooltip(ps: PresenceState): String = when (ps) {
    PresenceState.ONLINE -> "Online — host готов к подключению"
    PresenceState.OFFLINE -> "Offline — host не запущен"
    PresenceState.UNKNOWN -> "Статус неизвестен"
}
