package dev.termlink.product

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.termlink.crypto.BrokerCaptureCrypto
import dev.termlink.crypto.FrozenHistoryCrypto
import java.security.GeneralSecurityException
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith

/** Exercises the device's GCM provider with Windows-produced objects and invalid authentication tags. */
@RunWith(AndroidJUnit4::class)
class AuthenticatedHistoryCryptoInstrumentedTest {
    private fun vector(name: String): JsonObject =
        InstrumentationRegistry.getInstrumentation().context.assets.open(name).bufferedReader().use {
            Json.parseToJsonElement(it.readText()).jsonObject
        }

    private fun JsonObject.text(name: String) = getValue(name).jsonPrimitive.content
    private fun JsonObject.bytes(name: String) = text(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun JsonObject.id(name: String) = UUID.fromString(text(name))

    @Test fun frozenWindowsPageOpensAndRejectsChangedTag() {
        val v = vector("frozen-history-v1.json")
        val key = v.bytes("pairwise_key")
        val sealed = v.bytes("sealed_object")
        fun open(value: ByteArray) = FrozenHistoryCrypto.open(
            key, value, v.id("session_id"), v.id("pc_device_id"), v.id("recipient_device_id"),
            v.text("key_epoch").toLong(), v.bytes("token"), v.text("capture_id").toULong(),
            v.text("capture_revision").toULong(), v.text("snapshot_seq").toULong(),
            v.text("request_before_line_id").toULong(), v.text("max_lines").toInt(),
            v.text("max_cells").toInt(), v.text("max_utf8_bytes").toInt(),
            v.text("page_format_version").toInt(), v.text("state_contract"),
        )
        try {
            val page = open(sealed)
            try { assertArrayEquals(v.bytes("page"), page) } finally { page.fill(0) }
            val changed = dev.termlink.protocol.realtime.v1.FrozenHistoryCiphertextV1.parseFrom(sealed)
                .toBuilder().apply {
                    ciphertext = com.google.protobuf.ByteString.copyFrom(ciphertext.toByteArray().also {
                        it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
                    })
                }.build().toByteArray()
            assertThrows(GeneralSecurityException::class.java) { open(changed) }
        } finally { key.fill(0) }
    }

    @Test fun brokerWindowsCaptureOpensResealsAndRejectsChangedTag() {
        val v = vector("broker-capture-sealed-v1.json")
        val key = v.bytes("pairwise_key")
        val sealed = v.bytes("sealed_object")
        fun open(value: ByteArray) = BrokerCaptureCrypto.open(
            key, value, v.id("session_id"), v.id("pc_device_id"), v.id("recipient_device_id"),
            v.id("object_id"), v.text("source_sequence").toULong(), v.text("key_epoch").toLong(),
            v.text("canonical_length").toInt(), v.bytes("canonical_sha256"),
        )
        try {
            val canonical = open(sealed)
            try {
                assertArrayEquals(v.bytes("canonical"), canonical)
                assertArrayEquals(sealed, BrokerCaptureCrypto.seal(key, v.id("session_id"),
                    v.id("pc_device_id"), v.id("recipient_device_id"), v.id("object_id"),
                    v.text("source_sequence").toULong(), v.text("key_epoch").toLong(),
                    canonical, v.bytes("nonce")))
            } finally { canonical.fill(0) }
            assertThrows(GeneralSecurityException::class.java) {
                open(sealed.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() })
            }
        } finally { key.fill(0) }
    }
}
