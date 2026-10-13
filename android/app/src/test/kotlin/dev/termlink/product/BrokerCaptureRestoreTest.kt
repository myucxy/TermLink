package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.crypto.BrokerCaptureCrypto
import dev.termlink.history.HistoryRepository
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.network.production.ProductionHostHistoryObject
import dev.termlink.protocol.realtime.v1.BrokerCaptureSelectionV1
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.terminal.api.TerminalBrokerOutputReferenceV1
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class BrokerCaptureRestoreTest {
    private val vector by lazy {
        val path = listOf("protocol/test-vectors/broker-capture-sealed-v1.json",
            "../protocol/test-vectors/broker-capture-sealed-v1.json",
            "../../protocol/test-vectors/broker-capture-sealed-v1.json")
            .map(::File).first { it.isFile }
        Json.parseToJsonElement(path.readText()).jsonObject
    }

    private fun field(name: String) = vector.getValue(name).jsonPrimitive.content
    private fun bytes(name: String) = field(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun vectorId(name: String) = UUID.fromString(field(name))

    @Test
    fun inlineFailureResolvesWithoutHostRequestAndReleasesAdmission() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        try {
            val budget = TerminalBufferBudget { HeapState(128L * 1024 * 1024, 0) }
            val canonical = inlineFailure()
            val wire = TerminalBrokerOutputReferenceV1.inlineFailures(2, byteArrayOf(65, 66),
                canonical).use { it.encode() }
            canonical.fill(0)
            resolveBrokerOutput(2, wire, uuid(1), uuid(2), uuid(3), 7,
                ByteArray(32), HistoryRepository(database),
                request = { error("inline capture must never request Host History") },
                budget = budget).use { event ->
                assertArrayEquals(byteArrayOf(65, 66), event.copyOutput())
                assertEquals(136, event.copyCanonicalCaptures().size)
                assertEquals(1024L * 1024 + 136L * 8, budget.reservedBytes)
            }
            assertEquals(0L, budget.reservedBytes)
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking {
                    resolveBrokerOutput(3, wire, uuid(1), uuid(2), uuid(3), 7,
                        ByteArray(32), HistoryRepository(database),
                        request = { error("wrong sequence must not request Host History") },
                        budget = budget)
                }
            }
            assertEquals(0L, budget.reservedBytes)
            wire.fill(0)
        } finally { database.close() }
    }

    @Test
    fun externalCaptureAuthenticatesCachesAndReusesWithoutAnotherRpc() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val history = HistoryRepository(database, bufferBudget = budget)
        val sealed = bytes("sealed_object")
        val key = bytes("pairwise_key")
        val reference = externalReference()
        var requests = 0
        try {
            repeat(2) {
                resolveBrokerOutput(9, reference, vectorId("session_id"), vectorId("pc_device_id"),
                    vectorId("recipient_device_id"), 7, key, history, request = { selection ->
                        requests++
                        hostObject(selection, sealed.copyOf(), budget)
                    }, budget = budget).use { event ->
                    assertArrayEquals(byteArrayOf(65), event.copyOutput())
                    assertArrayEquals(bytes("canonical"), event.copyCanonicalCaptures())
                }
                assertEquals(0L, budget.reservedBytes)
            }
            assertEquals(1, requests)
        } finally {
            sealed.fill(0); key.fill(0); reference.fill(0); database.close()
        }
    }

    @Test
    fun unicode16BrokerCaptureCachesOnlyUnderItsNegotiatedContract() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val history = HistoryRepository(database, bufferBudget = budget)
        val key = bytes("pairwise_key")
        val canonical = bytes("canonical")
        val reference = externalReference()
        val state = BrokerCaptureCrypto.STATE_CONTRACT_V2
        val sealed = BrokerCaptureCrypto.seal(key, vectorId("session_id"), vectorId("pc_device_id"),
            vectorId("recipient_device_id"), vectorId("object_id"), 9uL, 7, canonical,
            stateContract = state)
        var requests = 0
        try {
            repeat(2) {
                resolveBrokerOutput(9, reference, vectorId("session_id"), vectorId("pc_device_id"),
                    vectorId("recipient_device_id"), 7, key, history, request = { selection ->
                        requests++
                        hostObject(selection, sealed.copyOf(), budget, state)
                    }, budget = budget, stateContract = state).use { event ->
                    assertArrayEquals(canonical, event.copyCanonicalCaptures())
                }
                assertEquals(0L, budget.reservedBytes)
            }
            assertEquals(1, requests)
        } finally {
            key.fill(0); canonical.fill(0); reference.fill(0); sealed.fill(0); database.close()
        }
    }

    @Test
    fun brokerCiphertextCannotCrossProfilesEvenWithExpectedKeyId() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val history = HistoryRepository(database, bufferBudget = budget)
        val key = bytes("pairwise_key")
        val reference = externalReference()
        val sessionId = vectorId("session_id")
        val captureId = vectorId("object_id")
        val canonical = bytes("canonical")
        val unicode16Sealed = BrokerCaptureCrypto.seal(key, sessionId,
            vectorId("pc_device_id"), vectorId("recipient_device_id"), captureId, 9uL, 7,
            canonical, stateContract = BrokerCaptureCrypto.STATE_CONTRACT_V2)
        try {
            for ((sealed, expectedState) in listOf(
                bytes("sealed_object") to BrokerCaptureCrypto.STATE_CONTRACT_V2,
                unicode16Sealed.copyOf() to BrokerCaptureCrypto.STATE_CONTRACT_V1,
            )) {
                try {
                    assertThrows(IllegalArgumentException::class.java) {
                        runBlocking {
                            resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                                vectorId("recipient_device_id"), 7, key, history, request = { selection ->
                                    hostObject(selection, sealed.copyOf(), budget, expectedState)
                                }, budget = budget, stateContract = expectedState)
                        }
                    }
                    assertNull(history.getBrokerCapture(sessionId, captureId, 7))
                    assertEquals(0L, budget.reservedBytes)
                } finally { sealed.fill(0) }
            }
        } finally {
            key.fill(0); reference.fill(0); canonical.fill(0); unicode16Sealed.fill(0); database.close()
        }
    }

    @Test
    fun failedCapturePersistenceNeverCreatesCacheAndExactRetryCanResolve() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val history = HistoryRepository(database, bufferBudget = budget)
        val sealed = bytes("sealed_object")
        val key = bytes("pairwise_key")
        val reference = externalReference()
        val sessionId = vectorId("session_id")
        val captureId = vectorId("object_id")
        var requests = 0
        try {
            database.openHelper.writableDatabase.execSQL(
                "CREATE TRIGGER fail_broker_insert BEFORE INSERT ON broker_captures " +
                    "BEGIN SELECT RAISE(ABORT, 'injected persistence failure'); END",
            )
            assertThrows(Exception::class.java) {
                runBlocking {
                    resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                        vectorId("recipient_device_id"), 7, key, history, request = { selection ->
                            requests++
                            hostObject(selection, sealed.copyOf(), budget)
                        }, budget = budget)
                }
            }
            assertNull(history.getBrokerCapture(sessionId, captureId, 7))
            assertEquals(0L, budget.reservedBytes)
            database.openHelper.writableDatabase.execSQL("DROP TRIGGER fail_broker_insert")

            resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                vectorId("recipient_device_id"), 7, key, history, request = { selection ->
                    requests++
                    hostObject(selection, sealed.copyOf(), budget)
                }, budget = budget).use { event ->
                assertArrayEquals(bytes("canonical"), event.copyCanonicalCaptures())
            }
            assertEquals(2, requests)
            assertEquals(0L, budget.reservedBytes)
            history.getBrokerCapture(sessionId, captureId, 7)?.close()
                ?: error("Broker capture was not persisted after retry")
        } finally {
            database.openHelper.writableDatabase.execSQL("DROP TRIGGER IF EXISTS fail_broker_insert")
            sealed.fill(0); key.fill(0); reference.fill(0); database.close()
        }
    }

    @Test
    fun tamperWrongRecipientCancellationAndCacheConflictFailClosedWithoutPermitLeak() = runBlocking {
        val database = TermLinkDatabase.inMemory(ApplicationProvider.getApplicationContext<Context>())
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val history = HistoryRepository(database, bufferBudget = budget)
        val sealed = bytes("sealed_object")
        val key = bytes("pairwise_key")
        val reference = externalReference()
        val sessionId = vectorId("session_id")
        val captureId = vectorId("object_id")
        try {
            for (corruptAt in listOf(sealed.lastIndex, 44)) {
                val corrupt = sealed.copyOf().also { it[corruptAt] = (it[corruptAt].toInt() xor 1).toByte() }
                assertThrows(Exception::class.java) {
                    runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                        vectorId("recipient_device_id"), 7, key, history,
                        request = { hostObject(it, corrupt, budget) }, budget = budget) }
                }
                assertNull(history.getBrokerCapture(sessionId, captureId, 7))
                assertEquals(0L, budget.reservedBytes)
            }
            assertThrows(Exception::class.java) {
                runBlocking { resolveBrokerOutput(9, reference, sessionId, UUID.randomUUID(),
                    vectorId("recipient_device_id"), 7, key, history,
                    request = { hostObject(it, sealed.copyOf(), budget) }, budget = budget) }
            }
            assertNull(history.getBrokerCapture(sessionId, captureId, 7))
            assertEquals(0L, budget.reservedBytes)
            assertThrows(Exception::class.java) {
                runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                    UUID.randomUUID(), 7, key, history,
                    request = { hostObject(it, sealed.copyOf(), budget) }, budget = budget) }
            }
            assertNull(history.getBrokerCapture(sessionId, captureId, 7))
            assertEquals(0L, budget.reservedBytes)
            assertThrows(Exception::class.java) {
                runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                    vectorId("recipient_device_id"), 8, key, history,
                    request = { hostObject(it, sealed.copyOf(), budget) }, budget = budget) }
            }
            assertNull(history.getBrokerCapture(sessionId, captureId, 8))
            assertEquals(0L, budget.reservedBytes)
            val wrongKey = key.copyOf().also { it[0] = (it[0].toInt() xor 1).toByte() }
            try {
                assertThrows(Exception::class.java) {
                    runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                        vectorId("recipient_device_id"), 7, wrongKey, history,
                        request = { hostObject(it, sealed.copyOf(), budget) }, budget = budget) }
                }
                assertNull(history.getBrokerCapture(sessionId, captureId, 7))
                assertEquals(0L, budget.reservedBytes)
            } finally { wrongKey.fill(0) }
            assertThrows(CancellationException::class.java) {
                runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                    vectorId("recipient_device_id"), 7, key, history,
                    request = { throw CancellationException("cancelled") }, budget = budget) }
            }
            assertEquals(0L, budget.reservedBytes)

            val otherId = UUID.randomUUID()
            val otherSealed = BrokerCaptureCrypto.seal(key, sessionId, vectorId("pc_device_id"),
                vectorId("recipient_device_id"), otherId, 9u, 7, bytes("canonical"))
            try {
                history.putBrokerCapture(sessionId, otherId, 9u, 7, 136, bytes("canonical_sha256"),
                    field("key_id"), otherSealed).use { }
                assertThrows(IllegalArgumentException::class.java) {
                    runBlocking { resolveBrokerOutput(9, reference, sessionId, vectorId("pc_device_id"),
                        vectorId("recipient_device_id"), 7, key, history,
                        request = { hostObject(it, sealed.copyOf(), budget) }, budget = budget) }
                }
                assertNull(history.getBrokerCapture(sessionId, captureId, 7))
                assertEquals(0L, budget.reservedBytes)
            } finally { otherSealed.fill(0) }
        } finally {
            sealed.fill(0); key.fill(0); reference.fill(0); database.close()
        }
    }

    private fun externalReference(): ByteArray = TerminalBrokerOutputReferenceV1.external(
        9, byteArrayOf(65), vectorId("object_id"), 136, bytes("canonical_sha256"),
    ).use { it.encode() }

    /** The network object's constructor is internal; reflect only at this cross-module test seam. */
    private fun hostObject(selection: BrokerCaptureSelectionV1, sealed: ByteArray,
        budget: TerminalBufferBudget,
        stateContract: String = BrokerCaptureCrypto.STATE_CONTRACT_V1): ProductionHostHistoryObject {
        val constructor = ProductionHostHistoryObject::class.java.declaredConstructors
            .single { it.parameterCount == 23 }
        constructor.isAccessible = true
        return constructor.newInstance(
            HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_BROKER_CAPTURE,
            vectorId("object_id"), 0L, 0, 0, 0L, 0L, 0L, 0L, false, 0L,
            9L, 9L, false, BrokerCaptureCrypto.keyId(vectorId("recipient_device_id"), 7, stateContract),
            "TERMLINK_BROKER_CAPTURE_V1", 1,
            MessageDigest.getInstance("SHA-256").digest(sealed), sealed,
            budget.reserve(sealed.size.toLong()), null, null, selection,
        ) as ProductionHostHistoryObject
    }

    private fun inlineFailure(): ByteArray = ByteBuffer.allocate(136)
        .order(ByteOrder.LITTLE_ENDIAN).apply {
            put("TLCAPV1\u0000".encodeToByteArray())
            putInt(32); putInt(1); putLong(2); putInt(136); putInt(0)
            putInt(1); putInt(96); putInt(1); putInt('f'.code)
            putLong(2); putInt(1); putInt(0)
            position(128); putInt(4); putInt(0)
        }.array()

    private fun uuid(value: Long) = UUID(0, value)
}
