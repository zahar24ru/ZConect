package com.zconect.viewer.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Gesture
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.DialogProperties
import com.zconect.viewer.R

private data class GestureItem(val icon: String, val titleRes: Int, val descRes: Int)

// Updated 2026-04-27 to match new gesture handler в RemoteScreen.kt:
// double-tap → real double-click, long-press+drag → window drag, 2-finger
// drag when zoomed → pan view.
private val trackpadGestures = listOf(
    GestureItem("👆", R.string.gesture_tap_title, R.string.gesture_tap_desc_trackpad),
    GestureItem("👆👆", R.string.gesture_doubletap_title, R.string.gesture_doubletap_desc_trackpad),
    GestureItem("✋", R.string.gesture_drag1_title, R.string.gesture_drag1_desc_trackpad),
    GestureItem("⏱", R.string.gesture_holddrag_title, R.string.gesture_holddrag_desc),
    GestureItem("⏱", R.string.gesture_longpress_title, R.string.gesture_longpress_desc),
    GestureItem("✌", R.string.gesture_2tap_title, R.string.gesture_2tap_desc),
    GestureItem("↕", R.string.gesture_2drag_title, R.string.gesture_2drag_desc),
    GestureItem("🔄", R.string.gesture_2drag_zoomed_title, R.string.gesture_2drag_zoomed_desc),
    GestureItem("➕➖", R.string.gesture_zoom_title, R.string.gesture_zoom_desc),
)

private val directTouchGestures = listOf(
    GestureItem("👆", R.string.gesture_tap_title, R.string.gesture_tap_desc_direct),
    GestureItem("👆👆", R.string.gesture_doubletap_title, R.string.gesture_doubletap_desc_direct),
    GestureItem("⏱", R.string.gesture_longpress_direct_title, R.string.gesture_longpress_direct_desc),
    GestureItem("✋", R.string.gesture_drag1_title, R.string.gesture_drag1_desc_direct),
    GestureItem("↕", R.string.gesture_2drag_title, R.string.gesture_2drag_desc),
)

private val toolbarTips = listOf(
    GestureItem("⬇", R.string.toolbar_tip_swipedown_title, R.string.toolbar_tip_swipedown_desc),
    GestureItem("📌", R.string.toolbar_tip_pin_title, R.string.toolbar_tip_pin_desc),
    GestureItem("👁", R.string.toolbar_tip_eye_title, R.string.toolbar_tip_eye_desc),
    GestureItem("🖱", R.string.toolbar_tip_mode_title, R.string.toolbar_tip_mode_desc),
    GestureItem("🔲", R.string.toolbar_tip_fit_title, R.string.toolbar_tip_fit_desc),
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GestureHelpDialog(
    useTrackpad: Boolean,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
        // Bug fix 2026-04-27: heightIn(max = 500.dp) обрезал нижнюю часть в
        // landscape (главный orientation для viewer'а — RemoteScreen
        // SCREEN_ORIENTATION_SENSOR_LANDSCAPE). Теперь dialog use большой
        // viewport (95%×90%) и весь контент scrollable — ничего не обрезается.
        modifier = Modifier
            .fillMaxWidth(0.95f)
            .fillMaxHeight(0.90f),
        title = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Gesture, contentDescription = null)
                Spacer(modifier = Modifier.width(8.dp))
                Text(stringResource(R.string.gestures_title))
            }
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
            ) {
                SectionTitle(stringResource(
                    if (useTrackpad) R.string.gestures_section_trackpad_current
                    else R.string.gestures_section_trackpad
                ))
                trackpadGestures.forEach { GestureRow(it) }

                Spacer(modifier = Modifier.height(12.dp))

                SectionTitle(stringResource(
                    if (!useTrackpad) R.string.gestures_section_directtouch_current
                    else R.string.gestures_section_directtouch
                ))
                directTouchGestures.forEach { GestureRow(it) }

                Spacer(modifier = Modifier.height(12.dp))

                SectionTitle(stringResource(R.string.gestures_section_toolbar))
                toolbarTips.forEach { GestureRow(it) }

                // Tail spacer чтобы последний пункт не упирался в bottom edge.
                Spacer(modifier = Modifier.height(8.dp))
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_got_it)) }
        }
    )
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        fontWeight = FontWeight.Bold,
        modifier = Modifier.padding(vertical = 6.dp)
    )
}

@Composable
private fun GestureRow(item: GestureItem) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp),
        verticalAlignment = Alignment.Top
    ) {
        Text(
            text = item.icon,
            fontSize = 22.sp,
            modifier = Modifier.width(40.dp)
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(item.titleRes),
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.SemiBold
            )
            Text(
                text = stringResource(item.descRes),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}
