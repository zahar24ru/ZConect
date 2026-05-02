package com.zconect.viewer.data

import android.content.Context
import android.content.SharedPreferences
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.util.UUID

@Serializable
data class Contact(
    val id: String = UUID.randomUUID().toString(),
    val name: String,
    val serverUrl: String,
    val loginCode: String,
    val passCode: String,
    /** UTC epoch millis последнего успешного подключения. 0 = никогда.
     * Mirror Windows Client.Contact.LastConnectedUtc — используется для сортировки
     * Recent row (top-N по убыванию) и human-readable "5 минут назад". */
    val lastConnectedUtc: Long = 0L,
)

/**
 * Persists a list of saved connections (contacts) in SharedPreferences as JSON.
 */
class AddressBook private constructor(private val prefs: SharedPreferences) {

    companion object {
        private const val PREFS_NAME = "zconect_address_book"
        private const val KEY_CONTACTS = "contacts"

        @Volatile
        private var instance: AddressBook? = null

        fun getInstance(context: Context): AddressBook {
            return instance ?: synchronized(this) {
                instance ?: AddressBook(
                    context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                ).also { instance = it }
            }
        }
    }

    private val json = Json { ignoreUnknownKeys = true }

    fun getAll(): List<Contact> {
        val raw = prefs.getString(KEY_CONTACTS, null) ?: return emptyList()
        return try {
            json.decodeFromString(kotlinx.serialization.builtins.ListSerializer(Contact.serializer()), raw)
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun save(contacts: List<Contact>) {
        val raw = json.encodeToString(
            kotlinx.serialization.builtins.ListSerializer(Contact.serializer()),
            contacts
        )
        prefs.edit().putString(KEY_CONTACTS, raw).apply()
    }

    fun add(contact: Contact) {
        val list = getAll().toMutableList()
        list.add(contact)
        save(list)
    }

    fun update(contact: Contact) {
        val list = getAll().toMutableList()
        val idx = list.indexOfFirst { it.id == contact.id }
        if (idx >= 0) {
            list[idx] = contact
            save(list)
        }
    }

    fun remove(id: String) {
        val list = getAll().filter { it.id != id }
        save(list)
    }

    /** Обновляет lastConnectedUtc на NOW для contact с заданным login_code.
     * Вызывается при успешном connect через этот контакт. */
    fun markConnected(loginCode: String) {
        val list = getAll().toMutableList()
        val idx = list.indexOfFirst { it.loginCode == loginCode }
        if (idx >= 0) {
            list[idx] = list[idx].copy(lastConnectedUtc = System.currentTimeMillis())
            save(list)
        }
    }
}
