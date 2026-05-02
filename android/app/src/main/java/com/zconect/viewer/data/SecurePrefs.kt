package com.zconect.viewer.data

import android.content.Context
import android.content.SharedPreferences
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * EncryptedSharedPreferences wrapper для sensitive data:
 * - login/passCode (auto-fill для last connect)
 * - per-contact saved unattended password
 *
 * Crypto: AES-256-SIV для keys + AES-256-GCM для values, master key
 * хранится в Android Keystore (hardware-backed на supported devices).
 *
 * Migration: первый раз когда вызывается, пытается перенести данные из
 * legacy plain SharedPreferences (см. AppSettings.PREFS_NAME). После
 * migration — old keys удаляются.
 *
 * Failure mode: если EncryptedSharedPreferences fails to init (corrupted
 * keystore, unsupported OS version, etc.) — falls back на in-memory map.
 * Это предотвращает crash, но credentials не persist'ятся между runs.
 */
class SecurePrefs(context: Context) {

    private val prefs: SharedPreferences = try {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            ENCRYPTED_PREFS_NAME,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    } catch (e: Exception) {
        // Fallback: если keystore corrupted (e.g. user reset device security
        // settings), use temporary in-memory storage. Better than crash.
        Log.e(TAG, "EncryptedSharedPreferences init failed, using fallback: ${e.message}")
        InMemorySharedPreferences()
    }

    fun getString(key: String, default: String? = null): String? =
        prefs.getString(key, default)

    fun putString(key: String, value: String?) {
        prefs.edit().apply {
            if (value == null) remove(key) else putString(key, value)
        }.apply()
    }

    fun remove(key: String) {
        prefs.edit().remove(key).apply()
    }

    fun contains(key: String): Boolean = prefs.contains(key)

    // ── Per-contact saved unattended password ──────────────────────────
    // Keyed by Contact.id (UUID). Allows silent reconnect через сохранённый
    // password — viewer не показывает dialog при каждом connect к saved contact.
    // Audit 2026-04-27 #4. Mirror WPF Contact.SavedUnattendedPassword.

    fun getContactPassword(contactId: String): String? =
        getString(CONTACT_PWD_PREFIX + contactId, null)

    fun setContactPassword(contactId: String, password: String?) {
        putString(CONTACT_PWD_PREFIX + contactId, password)
    }

    fun removeContactPassword(contactId: String) {
        remove(CONTACT_PWD_PREFIX + contactId)
    }

    /**
     * One-shot migration: copy plain values из legacy SharedPreferences
     * в encrypted, потом удалить из legacy.
     *
     * Вызывается из AppSettings.getInstance() при первом launch'е версии с
     * encrypted storage. Idempotent — если key уже migrated, no-op.
     */
    fun migrateFromPlain(plainPrefs: SharedPreferences, keys: List<String>) {
        for (key in keys) {
            // Skip если уже migrated (encrypted storage already has).
            if (prefs.contains(key)) {
                plainPrefs.edit().remove(key).apply()
                continue
            }
            val plainValue = plainPrefs.getString(key, null)
            if (plainValue != null) {
                prefs.edit().putString(key, plainValue).apply()
                plainPrefs.edit().remove(key).apply()
                Log.i(TAG, "Migrated $key to encrypted storage")
            }
        }
    }

    companion object {
        private const val TAG = "SecurePrefs"
        const val ENCRYPTED_PREFS_NAME = "zconect_secure"
        private const val CONTACT_PWD_PREFIX = "contact_pwd_"

        @Volatile private var instance: SecurePrefs? = null

        fun getInstance(context: Context): SecurePrefs {
            return instance ?: synchronized(this) {
                instance ?: SecurePrefs(context.applicationContext).also { instance = it }
            }
        }
    }
}

/**
 * Minimal SharedPreferences fallback для случая когда EncryptedSharedPreferences
 * не инициализируется. Storage только in-memory — credentials не persist.
 * Acceptable degradation для crash prevention.
 */
private class InMemorySharedPreferences : SharedPreferences {
    private val map = mutableMapOf<String, Any?>()

    override fun getAll(): Map<String, *> = map.toMap()
    override fun getString(key: String?, defValue: String?): String? = map[key] as? String ?: defValue
    override fun getStringSet(key: String?, defValues: MutableSet<String>?): MutableSet<String>? =
        @Suppress("UNCHECKED_CAST") (map[key] as? MutableSet<String>) ?: defValues
    override fun getInt(key: String?, defValue: Int): Int = map[key] as? Int ?: defValue
    override fun getLong(key: String?, defValue: Long): Long = map[key] as? Long ?: defValue
    override fun getFloat(key: String?, defValue: Float): Float = map[key] as? Float ?: defValue
    override fun getBoolean(key: String?, defValue: Boolean): Boolean = map[key] as? Boolean ?: defValue
    override fun contains(key: String?): Boolean = map.containsKey(key)

    override fun edit(): SharedPreferences.Editor = object : SharedPreferences.Editor {
        override fun putString(key: String?, value: String?) = apply { if (key != null) map[key] = value }
        override fun putStringSet(key: String?, values: MutableSet<String>?) = apply { if (key != null) map[key] = values }
        override fun putInt(key: String?, value: Int) = apply { if (key != null) map[key] = value }
        override fun putLong(key: String?, value: Long) = apply { if (key != null) map[key] = value }
        override fun putFloat(key: String?, value: Float) = apply { if (key != null) map[key] = value }
        override fun putBoolean(key: String?, value: Boolean) = apply { if (key != null) map[key] = value }
        override fun remove(key: String?) = apply { map.remove(key) }
        override fun clear() = apply { map.clear() }
        override fun commit(): Boolean = true
        override fun apply() {}
    }

    override fun registerOnSharedPreferenceChangeListener(listener: SharedPreferences.OnSharedPreferenceChangeListener?) {}
    override fun unregisterOnSharedPreferenceChangeListener(listener: SharedPreferences.OnSharedPreferenceChangeListener?) {}
}
