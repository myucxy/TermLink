package dev.termlink.crypto

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Stores X25519/Ed25519 private material only after wrapping it with a
 * non-exportable Android Keystore AES-GCM key. This works from API 26 where
 * native AndroidKeyStore X25519 support cannot be assumed.
 */
class DeviceKeyVault(context: Context, private val alias: String = "termlink-device-wrap-v1") {
    private val preferences = context.applicationContext.getSharedPreferences("termlink-device-identity", Context.MODE_PRIVATE)

    data class StoredIdentity(val deviceId: UUID, val identity: DeviceIdentity)

    @Synchronized
    fun loadOrCreate(): StoredIdentity {
        val deviceId = preferences.getString(KEY_DEVICE_ID, null)?.let(UUID::fromString) ?: UUID.randomUUID()
        val encoded = preferences.getString(KEY_WRAPPED, null)
        if (encoded != null) {
            val wrapped = Base64.decode(encoded, Base64.NO_WRAP)
            require(wrapped.size > IV_BYTES)
            val plaintext = decrypt(wrapped.copyOfRange(0, IV_BYTES), wrapped.copyOfRange(IV_BYTES, wrapped.size), deviceId)
            return try {
                StoredIdentity(deviceId, DeviceIdentity.fromPrivateMaterial(plaintext))
            } finally {
                plaintext.fill(0)
            }
        }

        val identity = DeviceIdentity.generate()
        val privateMaterial = identity.privateMaterial()
        return try {
            val (iv, ciphertext) = encrypt(privateMaterial, deviceId)
            check(preferences.edit()
                .putString(KEY_DEVICE_ID, deviceId.toString())
                .putString(KEY_WRAPPED, Base64.encodeToString(iv + ciphertext, Base64.NO_WRAP))
                .commit())
            StoredIdentity(deviceId, identity)
        } finally {
            privateMaterial.fill(0)
        }
    }

    @Synchronized
    fun clear() {
        preferences.edit().clear().commit()
        val keyStore = keyStore()
        if (keyStore.containsAlias(alias)) keyStore.deleteEntry(alias)
    }

    private fun encrypt(plaintext: ByteArray, deviceId: UUID): Pair<ByteArray, ByteArray> {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, wrappingKey())
        cipher.updateAAD(deviceId.toString().encodeToByteArray())
        return cipher.iv to cipher.doFinal(plaintext)
    }

    private fun decrypt(iv: ByteArray, ciphertext: ByteArray, deviceId: UUID): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, wrappingKey(), GCMParameterSpec(128, iv))
        cipher.updateAAD(deviceId.toString().encodeToByteArray())
        return cipher.doFinal(ciphertext)
    }

    private fun wrappingKey(): SecretKey {
        val keyStore = keyStore()
        (keyStore.getKey(alias, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
        const val KEY_DEVICE_ID = "device_id"
        const val KEY_WRAPPED = "wrapped_private_material"
    }
}
