package com.zconect.viewer.data

import android.content.Context
import android.content.SharedPreferences
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Ad-hoc подключение (не сохранённый контакт). Mirror Windows RecentConnection.
 * Хранится отдельно от AddressBook — не засоряет контакты.
 */
@Serializable
data class RecentConnection(
    /** login_code используется как dedup key. */
    val loginCode: String,
    val passCode: String,
    val serverUrl: String,
    /** UTC epoch millis */
    val lastConnectedUtc: Long,
)

/**
 * Persists ad-hoc подключения (без явного save-to-contacts). Cap на 10 последних
 * (старые автоматически удаляются). Используется для «Недавно подключённые» row
 * на ConnectScreen вместе с saved Contacts.
 *
 * Mirror Windows RecentHistoryService.
 */
class RecentHistory private constructor(private val prefs: SharedPreferences) {

    companion object {
        private const val PREFS_NAME = "zconect_recent_history"
        private const val KEY_ITEMS = "items"
        private const val MAX_ITEMS = 10

        @Volatile
        private var instance: RecentHistory? = null

        fun getInstance(context: Context): RecentHistory {
            return instance ?: synchronized(this) {
                instance ?: RecentHistory(
                    context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                ).also { instance = it }
            }
        }
    }

    private val json = Json { ignoreUnknownKeys = true }
    private val serializer = kotlinx.serialization.builtins.ListSerializer(RecentConnection.serializer())

    fun getAll(): List<RecentConnection> {
        val raw = prefs.getString(KEY_ITEMS, null) ?: return emptyList()
        return try {
            json.decodeFromString(serializer, raw)
        } catch (_: Exception) {
            emptyList()
        }
    }

    private fun save(items: List<RecentConnection>) {
        val raw = json.encodeToString(serializer, items)
        prefs.edit().putString(KEY_ITEMS, raw).apply()
    }

    /**
     * Записывает подключение. Если login_code уже существует в истории —
     * обновляет timestamp + коды. Иначе добавляет новый. Удаляет старые >MAX.
     *
     * @param skipIfInAddressBook если true и loginCode уже в saved Contacts —
     *   не добавляем в history (чтобы не дублировать). Но всё равно update'им
     *   Contact.lastConnectedUtc через AddressBook.markConnected().
     */
    fun record(
        loginCode: String,
        passCode: String,
        serverUrl: String,
        context: Context,
    ) {
        // Skip if это saved contact — там свой lastConnectedUtc
        val addressBook = AddressBook.getInstance(context)
        val existingContact = addressBook.getAll().firstOrNull { it.loginCode == loginCode }
        if (existingContact != null) {
            addressBook.markConnected(loginCode)
            return
        }

        val now = System.currentTimeMillis()
        val newItem = RecentConnection(loginCode, passCode, serverUrl, now)
        // Dedup by login_code — если уже был в history, замещаем (updates timestamp)
        val deduped = getAll().filter { it.loginCode != loginCode }
        val updated = (listOf(newItem) + deduped).take(MAX_ITEMS)
        save(updated)
    }

    /** Удаляет entry из history (например когда user сохранил как contact). */
    fun remove(loginCode: String) {
        val list = getAll().filter { it.loginCode != loginCode }
        save(list)
    }

    /** Очистить всю history. */
    fun clear() {
        prefs.edit().remove(KEY_ITEMS).apply()
    }
}

/**
 * Unified view-model для Recent row — объединяет saved Contacts и ad-hoc history.
 * Mirror Windows RecentItem. Sort'ится по lastConnectedUtc descending.
 */
data class RecentItem(
    val isSaved: Boolean,
    val displayName: String,
    val loginCode: String,
    val passCode: String,
    val serverUrl: String,
    val lastConnectedUtc: Long,
    val sourceContact: Contact?,
) {
    /** Первая буква имени для аватара. "?" для ephemeral без имени. */
    val firstLetter: String
        get() {
            if (!isSaved) return "?"
            if (displayName.isBlank()) return "?"
            return displayName.trim().substring(0, 1).uppercase()
        }

    companion object {
        /**
         * Combined list: saved contacts with non-zero lastConnected + history items.
         * Dedup by login_code (contact wins over history entry).
         * Sort DESC by lastConnectedUtc. Take top-N.
         */
        fun buildRecentList(
            contacts: List<Contact>,
            history: List<RecentConnection>,
            topN: Int = 5,
        ): List<RecentItem> {
            val savedByLogin = contacts
                .filter { it.lastConnectedUtc > 0 }
                .associateBy { it.loginCode }

            val savedItems = savedByLogin.values.map {
                RecentItem(
                    isSaved = true,
                    displayName = it.name,
                    loginCode = it.loginCode,
                    passCode = it.passCode,
                    serverUrl = it.serverUrl,
                    lastConnectedUtc = it.lastConnectedUtc,
                    sourceContact = it,
                )
            }

            val historyItems = history
                .filter { it.loginCode !in savedByLogin } // dedup
                .map {
                    RecentItem(
                        isSaved = false,
                        // «Сессия 5678» — последние 4 цифры login code как label
                        displayName = "Session ${it.loginCode.takeLast(4)}",
                        loginCode = it.loginCode,
                        passCode = it.passCode,
                        serverUrl = it.serverUrl,
                        lastConnectedUtc = it.lastConnectedUtc,
                        sourceContact = null,
                    )
                }

            return (savedItems + historyItems)
                .sortedByDescending { it.lastConnectedUtc }
                .take(topN)
        }
    }
}
