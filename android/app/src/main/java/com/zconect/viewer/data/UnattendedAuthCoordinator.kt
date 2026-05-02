package com.zconect.viewer.data

import android.util.Log
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec
import android.util.Base64

/**
 * Viewer-side unattended authentication handshake (RFC-style protocol mirror
 * WPF UnattendedAuthCoordinator + UnattendedAuthService).
 *
 * Protocol:
 *   1. Viewer → host: `unattended_auth_probe` (empty payload)
 *   2. Host → viewer: `unattended_auth_mode` { mode: "password_or_confirmation" | "confirmation",
 *                                              salt?: base64, nonce?: base64 }
 *   3. If mode == password:
 *      Viewer prompts user → computes proof = HMAC-SHA256(PBKDF2(password, salt), nonce)
 *      Viewer → host: `unattended_auth_password` { proof: base64 }
 *      Host verifies → `unattended_auth_result` { ok, reason?, attempts_left?,
 *                                                  lockout_remaining_sec?, lockout_tier? }
 *   4. If user skips: `unattended_auth_skip` → host shows confirmation dialog
 *
 * Crypto: PBKDF2-HMAC-SHA256 (100k iterations, 32-byte derived key) +
 * HMAC-SHA256 of nonce. Mirror WPF UnattendedAuthService.ComputeProof exactly.
 */
class UnattendedAuthCoordinator(
    private val signalingClient: SignalingClient,
    private val sessionId: String,
) {
    enum class Outcome {
        APPROVED,           // password verified — host won't show confirmation dialog
        FALLBACK,           // host responded confirmation_only OR probe timeout — viewer continues, host shows dialog
        LOCKED_OUT,         // wrong attempts triggered lockout — viewer should NOT retry immediately
        CANCELLED,          // user dismissed password dialog
    }

    data class Prompt(
        val isRetry: Boolean = false,
        val attemptsLeft: Int = 0,
        val isLockedOut: Boolean = false,
        val lockoutRemainingSec: Int = 0,
        val lockoutTier: Int = 0,
        val lastReason: String? = null,
        /** True если current connect — к saved contact (Address Book entry).
         * UI uses этот flag чтобы показать "Remember password" checkbox. Для
         * ad-hoc connect (manual login codes) checkbox не нужен — нечего save. */
        val canRemember: Boolean = false,
    )

    /** Result от password provider — password + flag хочет ли user сохранить. */
    data class PromptResult(
        val password: String?,
        val remember: Boolean = false,
    )

    /**
     * Run viewer handshake. Call AFTER WS connect, BEFORE sending offer.
     *
     * @param passwordProvider UI callback. Receives Prompt (retry info / lockout state),
     *   returns PromptResult (password + remember flag) or PromptResult(null) for cancel.
     * @param savedPassword Optional pre-saved password (per-contact). Если non-null,
     *   silent fast-path: send proof без UI prompt. Если wrong → fall through to
     *   interactive prompt и stale password cleared via [onSavedPasswordRejected].
     * @param onSavedPasswordRejected Callback fired when saved password rejected как
     *   wrong_password. Caller should remove stale entry from SecurePrefs.
     * @param onPasswordApproved Callback fired когда password accepted AND user
     *   wanted "remember" (PromptResult.remember=true). Receives password to save.
     * @param canRemember True если current connect — к saved Contact (Address Book).
     *   Для ad-hoc connect (manual codes) — false (нечего save).
     * @param probeTimeoutMs Fallback timeout for older hosts that don't respond to probe.
     */
    suspend fun run(
        messages: SharedFlow<SignalingMessage>,
        passwordProvider: suspend (Prompt) -> PromptResult,
        savedPassword: String? = null,
        onSavedPasswordRejected: (() -> Unit)? = null,
        onPasswordApproved: ((String) -> Unit)? = null,
        canRemember: Boolean = false,
        probeTimeoutMs: Long = 5000L,
    ): Outcome {
        // Step 1: send probe + wait mode response.
        val mode = sendProbeAndWaitMode(messages, probeTimeoutMs)
            ?: run {
                Log.i(TAG, "unattended_probe_timeout — fallback to confirmation")
                return Outcome.FALLBACK
            }

        if (mode.mode == "confirmation" || mode.mode.isEmpty()) {
            Log.i(TAG, "unattended_mode_confirmation — host doesn't require password")
            return Outcome.FALLBACK
        }
        if (mode.mode != "password_or_confirmation") {
            Log.w(TAG, "unattended_mode_unknown=${mode.mode} — fallback")
            return Outcome.FALLBACK
        }

        var saltBytes = Base64.decode(mode.salt, Base64.DEFAULT)
        var nonceBytes = Base64.decode(mode.nonce, Base64.DEFAULT)

        // Step 2: prompt loop — password → send → wait result. Retry on wrong_password,
        // re-probe on no_challenge (host nonce expired), break on lockout / ok / skip.
        var prompt = Prompt(canRemember = canRemember)
        // Saved password fast-path: try silent send first if available. If host
        // accepts → silent connect (user не видит dialog). If rejected → clear
        // stale + fall through to normal interactive prompt loop.
        var pendingSavedPassword: String? = savedPassword?.takeIf { it.isNotEmpty() }
        var rememberRequested = false
        while (true) {
            val password: String
            if (pendingSavedPassword != null) {
                // Silent fast-path — use saved password without UI prompt.
                password = pendingSavedPassword
                pendingSavedPassword = null
                Log.d(TAG, "unattended_using_saved_password")
            } else {
                val result = passwordProvider(prompt)
                if (result.password.isNullOrEmpty()) {
                    // User cancelled / chose to wait for confirmation — send skip.
                    signalingClient.send("unattended_auth_skip", buildJsonObject { })
                    Log.i(TAG, "unattended_skip_sent")
                    return Outcome.FALLBACK
                }
                password = result.password
                rememberRequested = result.remember && canRemember
            }

            val proof = computeProof(password, saltBytes, nonceBytes)
            signalingClient.send("unattended_auth_password", buildJsonObject {
                put("proof", JsonPrimitive(Base64.encodeToString(proof, Base64.NO_WRAP)))
            })
            Log.i(TAG, "unattended_password_sent")

            val result = waitResult(messages)
            if (result.ok) {
                Log.i(TAG, "unattended_auth_ok")
                if (rememberRequested) {
                    onPasswordApproved?.invoke(password)
                    Log.i(TAG, "unattended_password_remembered")
                }
                return Outcome.APPROVED
            }

            // Wrong / locked / no_challenge handling.
            if (result.reason == "locked_out") {
                Log.w(TAG, "unattended_locked_out remaining=${result.lockoutRemainingSec}s tier=${result.lockoutTier}")
                // Show lockout state to user (no retry — they'll reconnect when ready).
                passwordProvider(Prompt(
                    isRetry = true,
                    isLockedOut = true,
                    lockoutRemainingSec = result.lockoutRemainingSec,
                    lockoutTier = result.lockoutTier,
                    lastReason = "locked_out",
                ))
                return Outcome.LOCKED_OUT
            }

            if (result.reason == "no_challenge" || result.reason == "malformed_proof") {
                // Host nonce expired (e.g. after lockout reset). Silent re-probe + retry
                // with same password (user doesn't see the resync).
                Log.i(TAG, "unattended_stale_challenge_reprobing reason=${result.reason}")
                val freshMode = sendProbeAndWaitMode(messages, probeTimeoutMs)
                if (freshMode == null || freshMode.mode != "password_or_confirmation") {
                    Log.w(TAG, "unattended_reprobe_failed_fallback")
                    return Outcome.FALLBACK
                }
                saltBytes = Base64.decode(freshMode.salt, Base64.DEFAULT)
                nonceBytes = Base64.decode(freshMode.nonce, Base64.DEFAULT)

                val retryProof = computeProof(password, saltBytes, nonceBytes)
                signalingClient.send("unattended_auth_password", buildJsonObject {
                    put("proof", JsonPrimitive(Base64.encodeToString(retryProof, Base64.NO_WRAP)))
                })
                Log.i(TAG, "unattended_password_resent_after_reprobe")

                val retryResult = waitResult(messages)
                if (retryResult.ok) {
                    Log.i(TAG, "unattended_auth_ok_after_reprobe")
                    return Outcome.APPROVED
                }
                if (retryResult.reason == "locked_out") {
                    return Outcome.LOCKED_OUT
                }
                // Retry was wrong_password too — fall through to normal retry loop.
                if (retryResult.reason == "wrong_password") {
                    onSavedPasswordRejected?.invoke()
                }
                prompt = Prompt(
                    isRetry = true,
                    attemptsLeft = retryResult.attemptsLeft,
                    lastReason = retryResult.reason ?: "wrong_password",
                    canRemember = canRemember,
                )
                continue
            }

            // wrong_password / other — clear saved password (если была) и show retry prompt.
            if (result.reason == "wrong_password") {
                onSavedPasswordRejected?.invoke()
            }
            prompt = Prompt(
                isRetry = true,
                attemptsLeft = result.attemptsLeft,
                lastReason = result.reason ?: "wrong_password",
                canRemember = canRemember,
            )
            Log.i(TAG, "unattended_wrong_password attempts_left=${prompt.attemptsLeft}")
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private data class ModeResponse(
        val mode: String,
        val salt: String,
        val nonce: String,
    )

    private data class ResultResponse(
        val ok: Boolean,
        val reason: String?,
        val attemptsLeft: Int,
        val lockoutRemainingSec: Int,
        val lockoutTier: Int,
    )

    private suspend fun sendProbeAndWaitMode(
        messages: SharedFlow<SignalingMessage>,
        timeoutMs: Long,
    ): ModeResponse? {
        signalingClient.send("unattended_auth_probe", buildJsonObject { })
        Log.d(TAG, "unattended_probe_sent")

        return withTimeoutOrNull(timeoutMs) {
            val msg = messages.filter { it.type == "unattended_auth_mode" }.first()
            ModeResponse(
                mode = msg.payload["mode"]?.jsonPrimitive?.content ?: "",
                salt = msg.payload["salt"]?.jsonPrimitive?.content ?: "",
                nonce = msg.payload["nonce"]?.jsonPrimitive?.content ?: "",
            )
        }
    }

    private suspend fun waitResult(messages: SharedFlow<SignalingMessage>): ResultResponse {
        val msg = messages.filter { it.type == "unattended_auth_result" }.first()
        val p = msg.payload
        return ResultResponse(
            ok = p["ok"]?.jsonPrimitive?.content?.toBoolean() ?: false,
            reason = p["reason"]?.jsonPrimitive?.content,
            attemptsLeft = p["attempts_left"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0,
            lockoutRemainingSec = p["lockout_remaining_sec"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0,
            lockoutTier = p["lockout_tier"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0,
        )
    }

    /**
     * Compute HMAC-SHA256(PBKDF2(password, salt, 100k, 32), nonce).
     * Mirror exactly WPF UnattendedAuthService.ComputeProof:
     *   1. Pbkdf2(plaintext, salt) → 32-byte derived key (100k iterations SHA-256)
     *   2. HMAC-SHA256(derivedKey).ComputeHash(nonce) → proof
     */
    private fun computeProof(password: String, salt: ByteArray, nonce: ByteArray): ByteArray {
        // Step 1: PBKDF2-HMAC-SHA256, 100k iterations, 32-byte output (matches WPF's
        // Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256).GetBytes(32)).
        val spec = PBEKeySpec(password.toCharArray(), salt, PBKDF2_ITERATIONS, HASH_BITS)
        val factory = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
        val derived = factory.generateSecret(spec).encoded

        // Step 2: HMAC-SHA256 of nonce, keyed by derived password hash.
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(derived, "HmacSHA256"))
        return mac.doFinal(nonce)
    }

    companion object {
        private const val TAG = "UnattendedAuth"
        private const val PBKDF2_ITERATIONS = 100_000
        private const val HASH_BITS = 256 // 32-byte derived key
    }
}
