package dev.termlink.crypto

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
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
class DeviceKeyVault(context: Context, private val alias: String = "termlink-device-wrap-v2") {
    /**
     * Account identities intentionally live in a versioned preference file.  The
     * old implementation had one global key whose lifetime was coupled to a
     * username; it is deliberately not read or migrated here.
     */
    private val preferences = context.applicationContext.getSharedPreferences("termlink-device-identity-v2", Context.MODE_PRIVATE)

    data class StoredIdentity(val deviceId: UUID, val identity: DeviceIdentity)

    @Synchronized
    fun loadOrCreate(accountScope: String): StoredIdentity {
        val scope = canonicalScope(accountScope)
        val key = scopeKey(scope)
        val deviceKey = keyName(KEY_DEVICE_ID, key)
        val wrappedKey = keyName(KEY_WRAPPED, key)
        val scopedAlias = "$alias-$key"
        val deviceId = preferences.getString(deviceKey, null)?.let(UUID::fromString) ?: UUID.randomUUID()
        val encoded = preferences.getString(wrappedKey, null)
        if (encoded != null) {
            val wrapped = Base64.decode(encoded, Base64.NO_WRAP)
            require(wrapped.size > IV_BYTES)
            val plaintext = decrypt(
                wrapped.copyOfRange(0, IV_BYTES),
                wrapped.copyOfRange(IV_BYTES, wrapped.size),
                scope,
                deviceId,
                scopedAlias,
            )
            return try {
                StoredIdentity(deviceId, DeviceIdentity.fromPrivateMaterial(plaintext))
            } finally {
                plaintext.fill(0)
            }
        }

        val identity = DeviceIdentity.generate()
        val privateMaterial = identity.privateMaterial()
        return try {
            val (iv, ciphertext) = encrypt(privateMaterial, scope, deviceId, scopedAlias)
            check(preferences.edit()
                .putString(deviceKey, deviceId.toString())
                .putString(wrappedKey, Base64.encodeToString(iv + ciphertext, Base64.NO_WRAP))
                .commit())
            StoredIdentity(deviceId, identity)
        } finally {
            privateMaterial.fill(0)
        }
    }

    @Synchronized
    fun clear(accountScope: String) {
        val scope = canonicalScope(accountScope)
        val key = scopeKey(scope)
        val scopedAlias = "$alias-$key"
        preferences.edit()
            .remove(keyName(KEY_DEVICE_ID, key))
            .remove(keyName(KEY_WRAPPED, key))
            .commit()
        val keyStore = keyStore()
        if (keyStore.containsAlias(scopedAlias)) keyStore.deleteEntry(scopedAlias)
    }

    private fun encrypt(plaintext: ByteArray, scope: String, deviceId: UUID, scopedAlias: String): Pair<ByteArray, ByteArray> {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, wrappingKey(scopedAlias))
        cipher.updateAAD(aad(scope, deviceId))
        return cipher.iv to cipher.doFinal(plaintext)
    }

    private fun decrypt(iv: ByteArray, ciphertext: ByteArray, scope: String, deviceId: UUID, scopedAlias: String): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, wrappingKey(scopedAlias), GCMParameterSpec(128, iv))
        cipher.updateAAD(aad(scope, deviceId))
        return cipher.doFinal(ciphertext)
    }

    private fun wrappingKey(scopedAlias: String): SecretKey {
        val keyStore = keyStore()
        (keyStore.getKey(scopedAlias, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(scopedAlias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    private fun aad(scope: String, deviceId: UUID): ByteArray =
        "TERMLINK-DEVICE-IDENTITY-V2\n$scope\n$deviceId".toByteArray(StandardCharsets.UTF_8)

    private fun canonicalScope(value: String): String = value.trim().also {
        require(it.isNotEmpty()) { "Account scope is required" }
        require(it.length <= MAX_SCOPE_LENGTH) { "Account scope is too long" }
    }

    private fun scopeKey(scope: String): String =
        MessageDigest.getInstance("SHA-256").digest(scope.toByteArray(StandardCharsets.UTF_8))
            .joinToString("") { byte -> "%02x".format(byte.toInt() and 0xFF) }

    private fun keyName(prefix: String, scopeKey: String): String = "$prefix-$scopeKey"

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
        const val KEY_DEVICE_ID = "device_id"
        const val KEY_WRAPPED = "wrapped_private_material"
        const val MAX_SCOPE_LENGTH = 1024
    }
}
