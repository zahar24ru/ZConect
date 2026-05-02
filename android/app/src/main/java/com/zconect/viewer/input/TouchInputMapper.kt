package com.zconect.viewer.input

import android.view.KeyEvent
import com.zconect.viewer.webrtc.KeyboardInput
import com.zconect.viewer.webrtc.MouseInput
import com.zconect.viewer.webrtc.ScreenMeta
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * Maps Android touch events to remote mouse_input payloads.
 *
 * Two input modes (like Microsoft Remote Desktop):
 * - DIRECT TOUCH: finger = cursor, tap where you touch
 * - TRACKPAD: screen = touchpad, relative movement, tap = click at cursor pos
 */
class TouchInputMapper(
    private val onMouseInput: (MouseInput) -> Unit,
    private val onKeyboardInput: (KeyboardInput) -> Unit
) {
    enum class InputMode { DIRECT_TOUCH, TRACKPAD }

    // --- Configuration ---
    private var _screenMeta: ScreenMeta = ScreenMeta()
    var screenMeta: ScreenMeta
        get() = _screenMeta
        set(value) {
            val prev = _screenMeta
            _screenMeta = value
            // On display switch or capture area change — reset cursor to center of new display
            if (prev.displayId != value.displayId ||
                prev.captureX != value.captureX ||
                prev.captureY != value.captureY ||
                prev.width != value.width ||
                prev.height != value.height) {
                cursorRemoteX = value.captureX + value.width / 2
                cursorRemoteY = value.captureY + value.height / 2
            }
        }
    var fallbackScreenW: Int = 3000
    var fallbackScreenH: Int = 2000
    var videoWidth: Int = 1920
    var videoHeight: Int = 1080
    var useAspectFill: Boolean = true
    var inputMode: InputMode = InputMode.TRACKPAD

    // Trackpad sensitivity (pixels of finger movement → pixels of remote cursor movement)
    var trackpadSpeed: Float = 1.8f

    // --- Internal state ---
    // Current remote cursor position (for trackpad mode)
    // Uses Int.MIN_VALUE as "uninitialized" sentinel (because captureX can be negative for secondary displays)
    private var cursorRemoteX: Int = Int.MIN_VALUE
    private var cursorRemoteY: Int = Int.MIN_VALUE
    private val cursorInitialized: Boolean get() = cursorRemoteX != Int.MIN_VALUE

    // Drag state
    private var isDragging = false

    // Throttle
    private var lastMoveTimeMs: Long = 0
    private val moveThrottleMs = 12L  // ~83fps for smooth trackpad

    // ════════════════════════════════════════════════
    // Computed screen dimensions
    // ════════════════════════════════════════════════

    private val captureW: Int get() = if (screenMeta.width > 0) screenMeta.width else fallbackScreenW
    private val captureH: Int get() = if (screenMeta.height > 0) screenMeta.height else fallbackScreenH
    private val captureX: Int get() = screenMeta.captureX
    private val captureY: Int get() = screenMeta.captureY

    /** Initialize cursor to center of remote screen if not set */
    private fun ensureCursorInitialized() {
        if (!cursorInitialized) {
            cursorRemoteX = captureX + captureW / 2
            cursorRemoteY = captureY + captureH / 2
        }
    }

    /**
     * Reset state for display change.
     * Clears screenMeta (forcing fallback to video dimensions) and resets cursor to center.
     */
    fun resetForDisplayChange() {
        _screenMeta = ScreenMeta()
        cursorRemoteX = Int.MIN_VALUE // will be re-centered on next use
        cursorRemoteY = Int.MIN_VALUE
    }

    /** Get current remote cursor position for overlay drawing */
    fun getCursorScreenPosition(surfaceW: Float, surfaceH: Float): Pair<Float, Float>? {
        if (!cursorInitialized) return null
        // Reverse map: remote → surface coordinates
        val rx = (cursorRemoteX - captureX).toFloat() / captureW
        val ry = (cursorRemoteY - captureY).toFloat() / captureH
        return reverseMapToSurface(rx, ry, surfaceW, surfaceH)
    }

    // ════════════════════════════════════════════════
    // DIRECT TOUCH mode — finger = cursor
    // ════════════════════════════════════════════════

    private fun mapToRemote(touchX: Float, touchY: Float, surfaceW: Float, surfaceH: Float): Pair<Int, Int>? {
        if (videoWidth <= 0 || videoHeight <= 0 || surfaceW <= 0 || surfaceH <= 0) return null

        val vw = videoWidth.toFloat()
        val vh = videoHeight.toFloat()
        val rx: Float
        val ry: Float

        if (useAspectFill) {
            val fillScale = Math.max(surfaceW / vw, surfaceH / vh)
            val cropX = (vw * fillScale - surfaceW) / 2f
            val cropY = (vh * fillScale - surfaceH) / 2f
            rx = ((touchX + cropX) / (vw * fillScale)).coerceIn(0f, 1f)
            ry = ((touchY + cropY) / (vh * fillScale)).coerceIn(0f, 1f)
        } else {
            val fitScale = min(surfaceW / vw, surfaceH / vh)
            val displayW = vw * fitScale
            val displayH = vh * fitScale
            val offsetX = (surfaceW - displayW) / 2f
            val offsetY = (surfaceH - displayH) / 2f
            rx = ((touchX - offsetX) / displayW).coerceIn(0f, 1f)
            ry = ((touchY - offsetY) / displayH).coerceIn(0f, 1f)
        }

        val remoteX = (captureX + rx * captureW).roundToInt().coerceIn(captureX, captureX + captureW - 1)
        val remoteY = (captureY + ry * captureH).roundToInt().coerceIn(captureY, captureY + captureH - 1)
        return Pair(remoteX, remoteY)
    }

    private fun reverseMapToSurface(rx: Float, ry: Float, surfaceW: Float, surfaceH: Float): Pair<Float, Float> {
        val vw = videoWidth.toFloat()
        val vh = videoHeight.toFloat()
        return if (useAspectFill) {
            val fillScale = Math.max(surfaceW / vw, surfaceH / vh)
            val cropX = (vw * fillScale - surfaceW) / 2f
            val cropY = (vh * fillScale - surfaceH) / 2f
            Pair(rx * vw * fillScale - cropX, ry * vh * fillScale - cropY)
        } else {
            val fitScale = min(surfaceW / vw, surfaceH / vh)
            val displayW = vw * fitScale
            val displayH = vh * fitScale
            val offsetX = (surfaceW - displayW) / 2f
            val offsetY = (surfaceH - displayH) / 2f
            Pair(offsetX + rx * displayW, offsetY + ry * displayH)
        }
    }

    // ════════════════════════════════════════════════
    // TRACKPAD mode — relative movement, like laptop touchpad
    // ════════════════════════════════════════════════

    /** Move cursor by relative delta (trackpad mode) */
    private fun moveCursorRelative(deltaX: Float, deltaY: Float) {
        ensureCursorInitialized()
        // Scale delta by trackpad speed and remote/surface ratio
        val scaleX = captureW.toFloat() / 2000f * trackpadSpeed  // normalize to ~2000px surface
        val scaleY = captureH.toFloat() / 1200f * trackpadSpeed
        cursorRemoteX = (cursorRemoteX + deltaX * scaleX).roundToInt()
            .coerceIn(captureX, captureX + captureW - 1)
        cursorRemoteY = (cursorRemoteY + deltaY * scaleY).roundToInt()
            .coerceIn(captureY, captureY + captureH - 1)
    }

    // ════════════════════════════════════════════════
    // Public API — called from RemoteScreen gestures
    // ════════════════════════════════════════════════

    // --- Single tap ---
    fun sendTap(touchX: Float, touchY: Float, surfaceW: Float, surfaceH: Float): Boolean {
        return when (inputMode) {
            InputMode.DIRECT_TOUCH -> {
                val (x, y) = mapToRemote(touchX, touchY, surfaceW, surfaceH) ?: return false
                cursorRemoteX = x; cursorRemoteY = y
                onMouseInput(MouseInput("move", x, y))
                onMouseInput(MouseInput("down", x, y, button = 1))
                onMouseInput(MouseInput("up", x, y, button = 1))
                true
            }
            InputMode.TRACKPAD -> {
                ensureCursorInitialized()
                // Tap = click at current cursor position
                onMouseInput(MouseInput("down", cursorRemoteX, cursorRemoteY, button = 1))
                onMouseInput(MouseInput("up", cursorRemoteX, cursorRemoteY, button = 1))
                true
            }
        }
    }

    // --- Two-finger tap = right click ---
    fun sendRightClick(touchX: Float, touchY: Float, surfaceW: Float, surfaceH: Float): Boolean {
        return when (inputMode) {
            InputMode.DIRECT_TOUCH -> {
                val (x, y) = mapToRemote(touchX, touchY, surfaceW, surfaceH) ?: return false
                cursorRemoteX = x; cursorRemoteY = y
                onMouseInput(MouseInput("move", x, y))
                onMouseInput(MouseInput("down", x, y, button = 2))
                onMouseInput(MouseInput("up", x, y, button = 2))
                true
            }
            InputMode.TRACKPAD -> {
                ensureCursorInitialized()
                onMouseInput(MouseInput("down", cursorRemoteX, cursorRemoteY, button = 2))
                onMouseInput(MouseInput("up", cursorRemoteX, cursorRemoteY, button = 2))
                true
            }
        }
    }

    // --- Drag start ---
    fun sendDragStart(touchX: Float, touchY: Float, surfaceW: Float, surfaceH: Float) {
        when (inputMode) {
            InputMode.DIRECT_TOUCH -> {
                val (x, y) = mapToRemote(touchX, touchY, surfaceW, surfaceH) ?: return
                cursorRemoteX = x; cursorRemoteY = y
                onMouseInput(MouseInput("move", x, y))
                // No mouse down — this is just cursor movement for direct touch drag
            }
            InputMode.TRACKPAD -> {
                // In trackpad mode, single-finger drag = move cursor (no button)
                isDragging = false
            }
        }
    }

    // --- Drag move ---
    fun sendDragMove(touchX: Float, touchY: Float, deltaX: Float, deltaY: Float, surfaceW: Float, surfaceH: Float) {
        val now = System.currentTimeMillis()
        if (now - lastMoveTimeMs < moveThrottleMs) return
        lastMoveTimeMs = now

        when (inputMode) {
            InputMode.DIRECT_TOUCH -> {
                val (x, y) = mapToRemote(touchX, touchY, surfaceW, surfaceH) ?: return
                cursorRemoteX = x; cursorRemoteY = y
                onMouseInput(MouseInput("move", x, y))
            }
            InputMode.TRACKPAD -> {
                moveCursorRelative(deltaX, deltaY)
                onMouseInput(MouseInput("move", cursorRemoteX, cursorRemoteY))
            }
        }
    }

    // --- Drag end ---
    fun sendDragEnd() {
        when (inputMode) {
            InputMode.DIRECT_TOUCH -> {
                // Direct touch drag = click at last position
                if (cursorInitialized) {
                    onMouseInput(MouseInput("down", cursorRemoteX, cursorRemoteY, button = 1))
                    onMouseInput(MouseInput("up", cursorRemoteX, cursorRemoteY, button = 1))
                }
            }
            InputMode.TRACKPAD -> {
                // Just stop moving, no click
            }
        }
    }

    // --- Double tap + drag = left button drag (trackpad mode) ---
    fun sendLeftDragStart() {
        ensureCursorInitialized()
        isDragging = true
        onMouseInput(MouseInput("down", cursorRemoteX, cursorRemoteY, button = 1))
    }

    fun sendLeftDragMove(deltaX: Float, deltaY: Float) {
        val now = System.currentTimeMillis()
        if (now - lastMoveTimeMs < moveThrottleMs) return
        lastMoveTimeMs = now

        moveCursorRelative(deltaX, deltaY)
        onMouseInput(MouseInput("move", cursorRemoteX, cursorRemoteY))
    }

    fun sendLeftDragEnd() {
        if (isDragging && cursorInitialized) {
            onMouseInput(MouseInput("up", cursorRemoteX, cursorRemoteY, button = 1))
        }
        isDragging = false
    }

    // --- Two-finger scroll ---
    // scrollSpeed: lower = finer/smoother scroll
    var scrollSpeed: Float = 40f

    fun sendScroll(deltaY: Float) {
        ensureCursorInitialized()
        val delta = (deltaY * scrollSpeed).roundToInt()
        if (delta != 0) {
            onMouseInput(MouseInput("wheel", cursorRemoteX, cursorRemoteY, delta = delta))
        }
    }

    // ════════════════════════════════════════════════
    // Keyboard (unchanged)
    // ════════════════════════════════════════════════

    private val vkMap = mapOf(
        KeyEvent.KEYCODE_A to 0x41, KeyEvent.KEYCODE_B to 0x42,
        KeyEvent.KEYCODE_C to 0x43, KeyEvent.KEYCODE_D to 0x44,
        KeyEvent.KEYCODE_E to 0x45, KeyEvent.KEYCODE_F to 0x46,
        KeyEvent.KEYCODE_G to 0x47, KeyEvent.KEYCODE_H to 0x48,
        KeyEvent.KEYCODE_I to 0x49, KeyEvent.KEYCODE_J to 0x4A,
        KeyEvent.KEYCODE_K to 0x4B, KeyEvent.KEYCODE_L to 0x4C,
        KeyEvent.KEYCODE_M to 0x4D, KeyEvent.KEYCODE_N to 0x4E,
        KeyEvent.KEYCODE_O to 0x4F, KeyEvent.KEYCODE_P to 0x50,
        KeyEvent.KEYCODE_Q to 0x51, KeyEvent.KEYCODE_R to 0x52,
        KeyEvent.KEYCODE_S to 0x53, KeyEvent.KEYCODE_T to 0x54,
        KeyEvent.KEYCODE_U to 0x55, KeyEvent.KEYCODE_V to 0x56,
        KeyEvent.KEYCODE_W to 0x57, KeyEvent.KEYCODE_X to 0x58,
        KeyEvent.KEYCODE_Y to 0x59, KeyEvent.KEYCODE_Z to 0x5A,
        KeyEvent.KEYCODE_0 to 0x30, KeyEvent.KEYCODE_1 to 0x31,
        KeyEvent.KEYCODE_2 to 0x32, KeyEvent.KEYCODE_3 to 0x33,
        KeyEvent.KEYCODE_4 to 0x34, KeyEvent.KEYCODE_5 to 0x35,
        KeyEvent.KEYCODE_6 to 0x36, KeyEvent.KEYCODE_7 to 0x37,
        KeyEvent.KEYCODE_8 to 0x38, KeyEvent.KEYCODE_9 to 0x39,
        KeyEvent.KEYCODE_ENTER to 0x0D, KeyEvent.KEYCODE_ESCAPE to 0x1B,
        KeyEvent.KEYCODE_DEL to 0x08, KeyEvent.KEYCODE_FORWARD_DEL to 0x2E,
        KeyEvent.KEYCODE_TAB to 0x09, KeyEvent.KEYCODE_SPACE to 0x20,
        KeyEvent.KEYCODE_DPAD_LEFT to 0x25, KeyEvent.KEYCODE_DPAD_UP to 0x26,
        KeyEvent.KEYCODE_DPAD_RIGHT to 0x27, KeyEvent.KEYCODE_DPAD_DOWN to 0x28,
        KeyEvent.KEYCODE_SHIFT_LEFT to 0x10, KeyEvent.KEYCODE_SHIFT_RIGHT to 0x10,
        KeyEvent.KEYCODE_CTRL_LEFT to 0x11, KeyEvent.KEYCODE_CTRL_RIGHT to 0x11,
        KeyEvent.KEYCODE_ALT_LEFT to 0x12, KeyEvent.KEYCODE_ALT_RIGHT to 0x12,
        KeyEvent.KEYCODE_F1 to 0x70, KeyEvent.KEYCODE_F2 to 0x71,
        KeyEvent.KEYCODE_F3 to 0x72, KeyEvent.KEYCODE_F4 to 0x73,
        KeyEvent.KEYCODE_F5 to 0x74, KeyEvent.KEYCODE_F6 to 0x75,
        KeyEvent.KEYCODE_F7 to 0x76, KeyEvent.KEYCODE_F8 to 0x77,
        KeyEvent.KEYCODE_F9 to 0x78, KeyEvent.KEYCODE_F10 to 0x79,
        KeyEvent.KEYCODE_F11 to 0x7A, KeyEvent.KEYCODE_F12 to 0x7B,
        KeyEvent.KEYCODE_PAGE_UP to 0x21, KeyEvent.KEYCODE_PAGE_DOWN to 0x22,
        KeyEvent.KEYCODE_MOVE_HOME to 0x24, KeyEvent.KEYCODE_MOVE_END to 0x23,
        KeyEvent.KEYCODE_INSERT to 0x2D,
        KeyEvent.KEYCODE_MINUS to 0xBD, KeyEvent.KEYCODE_EQUALS to 0xBB,
        KeyEvent.KEYCODE_LEFT_BRACKET to 0xDB, KeyEvent.KEYCODE_RIGHT_BRACKET to 0xDD,
        KeyEvent.KEYCODE_SEMICOLON to 0xBA, KeyEvent.KEYCODE_APOSTROPHE to 0xDE,
        KeyEvent.KEYCODE_COMMA to 0xBC, KeyEvent.KEYCODE_PERIOD to 0xBE,
        KeyEvent.KEYCODE_SLASH to 0xBF, KeyEvent.KEYCODE_BACKSLASH to 0xDC,
        KeyEvent.KEYCODE_GRAVE to 0xC0,
    )

    fun handleKeyEvent(event: KeyEvent): Boolean {
        val vk = vkMap[event.keyCode] ?: return false
        val action = when (event.action) {
            KeyEvent.ACTION_DOWN -> "down"
            KeyEvent.ACTION_UP -> "up"
            else -> return false
        }
        onKeyboardInput(KeyboardInput(action, vk, alt = event.isAltPressed,
            ctrl = event.isCtrlPressed, shift = event.isShiftPressed, win = event.isMetaPressed))
        return true
    }

    fun sendCharacter(char: Char) {
        val vk: Int; val shift: Boolean
        when {
            char in 'a'..'z' -> { vk = 0x41 + (char - 'a'); shift = false }
            char in 'A'..'Z' -> { vk = 0x41 + (char - 'A'); shift = true }
            char in '0'..'9' -> { vk = 0x30 + (char - '0'); shift = false }
            char == ' ' -> { vk = 0x20; shift = false }
            char == '\n' || char == '\r' -> { vk = 0x0D; shift = false }
            char == '\t' -> { vk = 0x09; shift = false }
            char == '-' -> { vk = 0xBD; shift = false }  char == '_' -> { vk = 0xBD; shift = true }
            char == '=' -> { vk = 0xBB; shift = false }  char == '+' -> { vk = 0xBB; shift = true }
            char == '.' -> { vk = 0xBE; shift = false }  char == '>' -> { vk = 0xBE; shift = true }
            char == ',' -> { vk = 0xBC; shift = false }  char == '<' -> { vk = 0xBC; shift = true }
            char == '/' -> { vk = 0xBF; shift = false }  char == '?' -> { vk = 0xBF; shift = true }
            char == ';' -> { vk = 0xBA; shift = false }  char == ':' -> { vk = 0xBA; shift = true }
            char == '\'' -> { vk = 0xDE; shift = false } char == '"' -> { vk = 0xDE; shift = true }
            char == '[' -> { vk = 0xDB; shift = false }  char == '{' -> { vk = 0xDB; shift = true }
            char == ']' -> { vk = 0xDD; shift = false }  char == '}' -> { vk = 0xDD; shift = true }
            char == '\\' -> { vk = 0xDC; shift = false } char == '|' -> { vk = 0xDC; shift = true }
            char == '`' -> { vk = 0xC0; shift = false }  char == '~' -> { vk = 0xC0; shift = true }
            char == '!' -> { vk = 0x31; shift = true }   char == '@' -> { vk = 0x32; shift = true }
            char == '#' -> { vk = 0x33; shift = true }   char == '$' -> { vk = 0x34; shift = true }
            char == '%' -> { vk = 0x35; shift = true }   char == '^' -> { vk = 0x36; shift = true }
            char == '&' -> { vk = 0x37; shift = true }   char == '*' -> { vk = 0x38; shift = true }
            char == '(' -> { vk = 0x39; shift = true }   char == ')' -> { vk = 0x30; shift = true }
            else -> return
        }
        onKeyboardInput(KeyboardInput("down", vk, shift = shift))
        onKeyboardInput(KeyboardInput("up", vk, shift = shift))
    }

    fun sendVirtualKey(vk: Int, ctrl: Boolean = false, alt: Boolean = false, shift: Boolean = false, win: Boolean = false) {
        onKeyboardInput(KeyboardInput("down", vk, ctrl = ctrl, alt = alt, shift = shift, win = win))
        onKeyboardInput(KeyboardInput("up", vk, ctrl = ctrl, alt = alt, shift = shift, win = win))
    }
}
