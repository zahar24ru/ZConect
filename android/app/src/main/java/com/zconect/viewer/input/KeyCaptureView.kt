package com.zconect.viewer.input

import android.content.Context
import android.text.InputType
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.widget.EditText

/**
 * Invisible EditText с custom InputConnection для захвата IME input БЕЗ buffer'а.
 *
 * ## Зачем
 *
 * BasicTextField в Compose привязывает state к IME через TextFieldValue. Любой
 * write в state может триггерить IME re-emit → duplicate characters при
 * быстрой печати (Gboard / Samsung / SwiftKey показывали доубление).
 *
 * Пытались 3 раза исправить: autoCorrect=false + Password keyboard, anchor reset
 * pattern, AtomicReference + time dedup — ни один не помог на всех IME.
 *
 * ## Подход
 *
 * Используем **plain Android EditText** (tested, bug-free с IME) но override'им
 * `onCreateInputConnection` чтобы вернуть custom `KeyCaptureIC`. Наш IC:
 *
 * 1. **Не модифицирует** EditText buffer (super.commitText НЕ вызывается).
 *    EditText всегда остаётся empty — IME has no text to autocorrect over →
 *    не эмитит duplicates.
 * 2. Tracks composition state *только* внутри IC (single-threaded по contract
 *    InputConnection'а → synchronous, no race condition).
 * 3. `setComposingText` + `commitText` diff'ятся common-prefix → отправляем
 *    key events через callbacks.
 * 4. `deleteSurroundingText` + `KEYCODE_DEL` → backspace events.
 * 5. `KEYCODE_ENTER` → Enter.
 *
 * Это bulletproof approach который используется в VNC Viewer, Microsoft RDP.
 *
 * ## Usage в Compose
 *
 * ```kotlin
 * AndroidView(
 *     factory = { ctx ->
 *         KeyCaptureView(ctx).apply {
 *             onCharacter = { ch -> touchMapper.sendCharacter(ch) }
 *             onBackspace = { touchMapper.sendVirtualKey(0x08) }
 *             onEnter = { touchMapper.sendVirtualKey(0x0D) }
 *         }
 *     },
 *     modifier = Modifier.size(1.dp)
 * )
 * ```
 */
class KeyCaptureView(context: Context) : EditText(context) {

    /** Called for every printable character typed (включая из autocorrect replacement). */
    var onCharacter: (Char) -> Unit = {}

    /** Called on backspace (hw DEL key или deleteSurroundingText). */
    var onBackspace: () -> Unit = {}

    /** Called on Enter / IME done action. */
    var onEnter: () -> Unit = {}

    init {
        // Single line, no suggestions / autocorrect.
        inputType = InputType.TYPE_CLASS_TEXT or
                InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS or
                InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
        isFocusable = true
        isFocusableInTouchMode = true
        setBackgroundColor(android.graphics.Color.TRANSPARENT)
        setTextColor(android.graphics.Color.TRANSPARENT)
        setCursorVisible(false)
        textSize = 1f
    }

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
        outAttrs.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        outAttrs.inputType = inputType
        // Вернуть our custom IC напрямую (НЕ wrap super.onCreateInputConnection,
        // чтобы буфер EditText не синхронизировался с IME — нам не нужен text state).
        return KeyCaptureIC(this)
    }

    /** Custom InputConnection — вся магия здесь. State (composingText) live
     * внутри IC, не в View — безопасно от Compose state deferral. */
    private inner class KeyCaptureIC(view: View) : BaseInputConnection(view, false) {

        /** Текущий composing text (underlined в IME). Обновляется
         * setComposingText, clear'ится на commit/finishComposing. */
        private var composingText: String = ""

        /** Last emitted character + timestamp для emission-level dedup.
         * Защита от buggy IMEs которые вызывают InputConnection methods дважды
         * (Samsung, некоторые Gboard builds). Если тот же char приходит <40ms
         * после предыдущего — drop. */
        private var lastEmittedChar: Char? = null
        private var lastEmitTimeMs: Long = 0L

        override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val newComposing = text?.toString() ?: ""
            Log.d(TAG, "IC.setComposing new=\"$newComposing\" cur=\"$composingText\" pos=$newCursorPosition")
            applyDiff(oldText = composingText, newText = newComposing, tag = "setComposing")
            composingText = newComposing
            return true
        }

        override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val committed = text?.toString() ?: ""
            Log.d(TAG, "IC.commitText new=\"$committed\" cur=\"$composingText\" pos=$newCursorPosition")

            // Edge case: empty commitText = IME хочет finalize composing без смены
            // текста. Characters уже отправлены во время setComposingText — просто
            // clear state. НЕ делаем backspaces.
            if (committed.isEmpty()) {
                composingText = ""
                return true
            }
            // Commit == composing → уже отправлено, no-op
            if (committed != composingText) {
                applyDiff(oldText = composingText, newText = committed, tag = "commit")
            }
            composingText = ""
            return true
        }

        override fun finishComposingText(): Boolean {
            Log.d(TAG, "IC.finishComposing cur=\"$composingText\"")
            // User закончил composing (cursor moved away / IME switched etc.)
            // Characters уже отправлены во время setComposingText.
            composingText = ""
            return true
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            Log.d(TAG, "IC.deleteSurrounding before=$beforeLength after=$afterLength composing=\"$composingText\"")
            // Adjust composing если удаляют внутри него
            if (composingText.isNotEmpty()) {
                val removeFromComposing = minOf(beforeLength, composingText.length)
                composingText = composingText.dropLast(removeFromComposing)
                repeat(removeFromComposing) { emitBackspace() }
                val remainingBackspaces = beforeLength - removeFromComposing
                if (remainingBackspaces > 0) {
                    repeat(remainingBackspaces) { emitBackspace() }
                }
            } else {
                repeat(beforeLength) { emitBackspace() }
            }
            return true
        }

        override fun sendKeyEvent(event: KeyEvent?): Boolean {
            if (event?.action == KeyEvent.ACTION_DOWN) {
                Log.d(TAG, "IC.sendKeyEvent code=${event.keyCode}")
                when (event.keyCode) {
                    KeyEvent.KEYCODE_DEL -> {
                        if (composingText.isNotEmpty()) {
                            composingText = composingText.dropLast(1)
                        }
                        emitBackspace()
                        return true
                    }
                    KeyEvent.KEYCODE_ENTER -> {
                        onEnter()
                        composingText = ""
                        return true
                    }
                }
            }
            return super.sendKeyEvent(event)
        }

        /** Common-prefix diff → send character / backspace events. */
        private fun applyDiff(oldText: String, newText: String, tag: String) {
            if (oldText == newText) return

            var commonPrefix = 0
            val minLen = minOf(oldText.length, newText.length)
            while (commonPrefix < minLen && oldText[commonPrefix] == newText[commonPrefix]) {
                commonPrefix++
            }
            val toDelete = oldText.length - commonPrefix
            val toType = newText.substring(commonPrefix)

            Log.d(TAG, "diff_apply tag=$tag old=\"$oldText\" new=\"$newText\" del=$toDelete type=\"$toType\"")

            repeat(toDelete) { emitBackspace() }
            for (ch in toType) {
                emitCharacter(ch)
            }
        }

        /**
         * Emit character через callback С защитой от buggy-IME double-emission.
         * Если тот же char приходит <40ms после предыдущего — dedup (skip).
         *
         * Классический dupe pattern: некоторые IMEs (особенно на Samsung firmware)
         * вызывают commitText + setComposingText в одной InputConnection-транзакции
         * для одного keystroke → наш diff'ер correctly видит это как 2 events с
         * одинаковым text, но 40ms гарантия на клавиатурный ввод:
         *   - Human max typing speed ~10 chars/sec = 100ms per char
         *   - Key repeat rate ~30ms — но это для one char, не одинаковых
         *   - Так что same char within 40ms — ТОЧНО IME bug, не user.
         */
        private fun emitCharacter(ch: Char) {
            val now = System.currentTimeMillis()
            val dt = now - lastEmitTimeMs
            if (ch == lastEmittedChar && dt < 40) {
                Log.w(TAG, "emit_dedup_char char='$ch' dt=${dt}ms (IME duplicate detected)")
                return
            }
            Log.d(TAG, "emit_char char='$ch' dt=${dt}ms")
            onCharacter(ch)
            lastEmittedChar = ch
            lastEmitTimeMs = now
        }

        /** Emit backspace с аналогичной защитой. */
        private fun emitBackspace() {
            val now = System.currentTimeMillis()
            val dt = now - lastEmitTimeMs
            if (lastEmittedChar == null && dt < 40) {
                // lastEmittedChar=null signals "previous was backspace" — dedup if <40ms
                Log.w(TAG, "emit_dedup_bs dt=${dt}ms (IME duplicate)")
                return
            }
            Log.d(TAG, "emit_bs dt=${dt}ms")
            onBackspace()
            lastEmittedChar = null // signals "last was backspace"
            lastEmitTimeMs = now
        }
    }

    companion object {
        private const val TAG = "KeyCaptureView"
    }
}
